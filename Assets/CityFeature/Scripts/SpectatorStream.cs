using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CityFeature
{
    /// <summary>
    /// Spectator view for a projector, without Meta casting or Wi-Fi.
    /// A second, mono camera follows the headset with smoothing (no head shake, no roll),
    /// renders a small image a few times per second, and serves it as an MJPEG stream on
    /// http://localhost:8090 inside the headset. Over USB: `adb forward tcp:8090 tcp:8090`,
    /// then open http://localhost:8090 in a browser on the laptop (DemoTools has a .bat).
    /// The page fetches one frame after another (/frame?after=N), so a stalled headset shows 'Waiting…'.
    /// Nothing is rendered while nobody is watching.
    /// </summary>
    public class SpectatorStream : MonoBehaviour
    {
        public const int Port = 8090;

        [SerializeField] private int width = 640;
        [SerializeField] private int height = 360;
        [SerializeField] private float fps = 20f;
        [SerializeField, Range(30, 95)] private int jpegQuality = 70;
        [SerializeField] private float fieldOfView = 75f;
        [Tooltip("How quickly the spectator camera catches up with the head (higher = snappier).")]
        [SerializeField] private float followPosition = 10f;
        [SerializeField] private float followRotation = 5f;
        [SerializeField] private float maxPitch = 35f;
        [Tooltip("A head jump bigger than this (m) is a teleport: snap instead of gliding.")]
        [SerializeField] private float snapDistance = 1.5f;

        private Camera head;
        private Camera cam;
        private RenderTexture rt;
        private float nextRender;
        private bool readbackPending;
        private readonly List<Renderer> hiddenDuringRender = new List<Renderer>();
        private Vector3 smoothPos;
        private float smoothYaw, smoothPitch;
        private bool hasPose;

        // Server state (shared with network threads).
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;
        private long lastViewerTicks;   // last time a viewer asked for a frame
        private readonly object frameLock = new object();
        private byte[] latestJpeg;
        private int frameId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (FindAnyObjectByType<SpectatorStream>() != null) return;
            var go = new GameObject("Spectator Stream");
            DontDestroyOnLoad(go);
            go.AddComponent<SpectatorStream>();
        }

        private void OnEnable()
        {
            StartServer();
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            RenderPipelineManager.endCameraRendering += OnEndCamera;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            RenderPipelineManager.endCameraRendering -= OnEndCamera;
            StopServer();
        }

        private void OnDestroy()
        {
            if (rt != null) { rt.Release(); Destroy(rt); }
        }

        private void EnsureCamera()
        {
            if (cam != null) return;
            rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { name = "Spectator RT", antiAliasing = 2 };
            rt.Create();

            var go = new GameObject("Spectator Camera");
            go.transform.SetParent(transform, false);
            cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.targetTexture = rt;
            cam.fieldOfView = fieldOfView;
            cam.depth = -10;
            var data = cam.GetUniversalAdditionalCameraData();
            data.allowXRRendering = false;
            data.renderShadows = false;          // the big saving on Quest
            data.renderPostProcessing = false;
            data.requiresDepthTexture = false;
            data.requiresColorTexture = false;
            data.antialiasing = AntialiasingMode.None;
        }

        private void LateUpdate()
        {
            if (head == null || !head.isActiveAndEnabled) { head = Camera.main; if (head == null) return; }
            UpdatePose();

            bool watched = DateTime.UtcNow.Ticks - Interlocked.Read(ref lastViewerTicks) < TimeSpan.TicksPerSecond * 3;
            if (!watched || readbackPending) return;
            if (Time.unscaledTime < nextRender) return;
            nextRender = Time.unscaledTime + 1f / Mathf.Max(1f, fps);

            EnsureCamera();
            cam.transform.SetPositionAndRotation(smoothPos, Quaternion.Euler(smoothPitch, smoothYaw, 0f));
            cam.nearClipPlane = head.nearClipPlane;
            cam.farClipPlane = head.farClipPlane;
            cam.cullingMask = head.cullingMask;
            cam.backgroundColor = head.backgroundColor;
            cam.clearFlags = head.clearFlags;
            cam.enabled = true;   // renders once this frame, switched off again in OnEndCamera
        }

        /// <summary>Smoothed head pose: glides position, eases yaw/pitch, never rolls; snaps on teleports.</summary>
        private void UpdatePose()
        {
            Transform h = head.transform;
            Vector3 fwd = h.forward;
            float yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg, -maxPitch, maxPitch);

            if (!hasPose || (h.position - smoothPos).sqrMagnitude > snapDistance * snapDistance)
            {
                smoothPos = h.position; smoothYaw = yaw; smoothPitch = pitch; hasPose = true;
                return;
            }
            float dt = Time.unscaledDeltaTime;
            smoothPos = Vector3.Lerp(smoothPos, h.position, 1f - Mathf.Exp(-followPosition * dt));
            float k = 1f - Mathf.Exp(-followRotation * dt);
            smoothYaw += Mathf.DeltaAngle(smoothYaw, yaw) * k;
            smoothPitch = Mathf.Lerp(smoothPitch, pitch, k);
        }

        private void OnBeginCamera(ScriptableRenderContext ctx, Camera c)
        {
            if (c != cam) return;
            // Things glued to the headset that look wrong from the smoothed camera (comfort vignette).
            hiddenDuringRender.Clear();
            if (head == null) return;
            foreach (var r in head.GetComponentsInChildren<Renderer>())
                if (r.enabled && r.gameObject.name.IndexOf("Vignette", StringComparison.OrdinalIgnoreCase) >= 0)
                { r.enabled = false; hiddenDuringRender.Add(r); }
        }

        private void OnEndCamera(ScriptableRenderContext ctx, Camera c)
        {
            if (c != cam) return;
            foreach (var r in hiddenDuringRender) if (r != null) r.enabled = true;
            hiddenDuringRender.Clear();
            cam.enabled = false;
            readbackPending = true;
            AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, OnReadback);
        }

        private void OnReadback(AsyncGPUReadbackRequest req)
        {
            readbackPending = false;
            if (req.hasError || !running) return;
            byte[] raw = req.GetData<byte>().ToArray();
            int w = req.width, h = req.height, q = jpegQuality;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    byte[] jpg = ImageConversion.EncodeArrayToJPG(raw, GraphicsFormat.R8G8B8A8_SRGB, (uint)w, (uint)h, 0, q);
                    lock (frameLock) { latestJpeg = jpg; frameId++; Monitor.PulseAll(frameLock); }
                }
                catch (Exception e) { Debug.LogWarning("[Spectator] JPEG encode failed: " + e.Message); }
            });
        }

        // ---------------- Tiny HTTP / MJPEG server ----------------

        private void StartServer()
        {
            if (running) return;
            try
            {
                listener = new TcpListener(IPAddress.Any, Port);
                listener.Start();
                running = true;
                acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "Spectator accept" };
                acceptThread.Start();
                Debug.Log($"[Spectator] Stream ready on port {Port} (adb forward tcp:{Port} tcp:{Port}, then http://localhost:{Port})");
            }
            catch (Exception e) { Debug.LogWarning("[Spectator] Could not start the stream server: " + e.Message); }
        }

        private void StopServer()
        {
            running = false;
            try { listener?.Stop(); } catch { }
            lock (frameLock) Monitor.PulseAll(frameLock);
        }

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { break; }
                var t = new Thread(() => Serve(client)) { IsBackground = true, Name = "Spectator client" };
                t.Start();
            }
        }

        private void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    // Keep-alive: the viewer asks for one frame after another on the same connection.
                    while (running)
                    {
                        string path = ReadRequestPath(stream, out bool keepAlive);
                        if (path == null) return;
                        if (path.StartsWith("/frame")) ServeFrame(stream, path, keepAlive);
                        else WriteBody(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page), keepAlive, null);
                        if (!keepAlive) return;
                    }
                }
                catch { /* viewer went away */ }
            }
        }

        /// <summary>/frame?after=N waits (max 1 s) for a frame newer than N and returns it as a JPEG.</summary>
        private void ServeFrame(NetworkStream stream, string path, bool keepAlive)
        {
            Interlocked.Exchange(ref lastViewerTicks, DateTime.UtcNow.Ticks);
            int after = -1;
            int i = path.IndexOf("after=", StringComparison.Ordinal);
            if (i >= 0) int.TryParse(path.Substring(i + 6).Split('&')[0], out after);

            byte[] jpg; int id;
            lock (frameLock)
            {
                if (after > frameId) after = -1;   // the app restarted: start over
                // Loop: Wait can wake without a new frame (pulses for other viewers, spurious wake-ups).
                var deadline = DateTime.UtcNow.AddSeconds(1);
                while (running && frameId <= after)
                {
                    int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) break;
                    Monitor.Wait(frameLock, left);
                }
                jpg = latestJpeg; id = frameId;
            }
            if (jpg == null || id <= after) { WriteBody(stream, "204 No Content", "text/plain", new byte[0], keepAlive, null); return; }
            WriteBody(stream, "200 OK", "image/jpeg", jpg, keepAlive, "X-Frame: " + id + "\r\n");
        }

        private static string ReadRequestPath(NetworkStream stream, out bool keepAlive)
        {
            keepAlive = false;
            var sb = new StringBuilder();
            var buf = new byte[1];
            stream.ReadTimeout = 10000;
            while (sb.Length < 8192)
            {
                if (stream.Read(buf, 0, 1) <= 0) return null;
                sb.Append((char)buf[0]);
                if (sb.Length >= 4 && sb[sb.Length - 1] == '\n' && sb[sb.Length - 3] == '\n') break;
            }
            string req = sb.ToString();
            string[] parts = req.Split('\n')[0].Split(' ');
            keepAlive = req.IndexOf("Connection: close", StringComparison.OrdinalIgnoreCase) < 0
                        && parts.Length >= 3 && parts[2].Trim() == "HTTP/1.1";
            return parts.Length >= 2 ? parts[1] : "/";
        }

        private static void WriteBody(NetworkStream stream, string status, string type, byte[] body, bool keepAlive, string extraHeaders)
        {
            byte[] h = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-cache, no-store\r\n" +
                (extraHeaders ?? "") + (keepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n") + "\r\n");
            stream.Write(h, 0, h.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
        }

        // Fullscreen viewer: black page, picture fitted, reconnects by itself (headset off, app restart).
        private const string Page = @"<!DOCTYPE html><html><head><meta charset='utf-8'><title>B&amp;D City</title>
<style>html,body{margin:0;height:100%;background:#000;overflow:hidden;cursor:none}
img{width:100vw;height:100vh;object-fit:contain;display:block}
#wait{position:fixed;inset:0;display:flex;align-items:center;justify-content:center;color:#666;font:20px sans-serif}</style></head>
<body><div id='wait'>Waiting for the headset&hellip;</div><img id='v' alt=''>
<script>
const v=document.getElementById('v'),w=document.getElementById('wait');let last=-1,url=null;
async function loop(){
  for(;;){
    try{
      const c=new AbortController(),t=setTimeout(()=>c.abort(),4000);
      const r=await fetch('/frame?after='+last,{cache:'no-store',signal:c.signal});clearTimeout(t);
      if(r.status===200){
        last=parseInt(r.headers.get('X-Frame')||'-1');
        const b=await r.blob(),u=URL.createObjectURL(b);
        v.src=u;if(url)URL.revokeObjectURL(url);url=u;w.style.display='none';
      }
    }catch(e){w.style.display='flex';last=-1;await new Promise(r=>setTimeout(r,1000));}
  }
}
document.addEventListener('dblclick',()=>{document.fullscreenElement?document.exitFullscreen():document.documentElement.requestFullscreen();});
loop();
</script></body></html>";
    }
}
