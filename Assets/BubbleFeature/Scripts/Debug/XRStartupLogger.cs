using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace BubbleFeature.DebugTools
{
    /// <summary>
    /// Stage 0 helper. Put it on an empty GameObject in the test scene.
    /// Logs (prefix "[XR]") which XR loader and OpenXR runtime are active,
    /// and every headset/controller that connects or disconnects.
    /// Safe to delete once VR runs reliably.
    /// </summary>
    public class XRStartupLogger : MonoBehaviour
    {
        IEnumerator Start()
        {
            // Wait one frame so XR has finished starting up.
            yield return null;

            XRGeneralSettings settings = XRGeneralSettings.Instance;
            XRLoader loader = settings != null && settings.Manager != null
                ? settings.Manager.activeLoader
                : null;

            if (loader == null)
            {
                Debug.LogWarning("[XR] No XR loader active: running flat (no headset). " +
                                 "Check: Link running, OpenXR ticked on the Windows tab, " +
                                 "'Initialize XR on Startup' ticked.");
            }
            else
            {
                Debug.Log($"[XR] Loader: {loader.name}");
                // Should mention Oculus / Meta when Link is the active runtime.
                Debug.Log($"[XR] OpenXR runtime: {OpenXRRuntime.name} {OpenXRRuntime.version}");
            }

            // Log XR devices that were already connected before this script started.
            foreach (InputDevice device in InputSystem.devices)
                LogDevice(device, "present");
        }

        void OnEnable()  => InputSystem.onDeviceChange += OnDeviceChange;
        void OnDisable() => InputSystem.onDeviceChange -= OnDeviceChange;

        void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (change == InputDeviceChange.Added || change == InputDeviceChange.Removed)
                LogDevice(device, change.ToString().ToLower());
        }

        static void LogDevice(InputDevice device, string state)
        {
            // Only headset and controllers; skip keyboard, mouse, etc.
            if (device is XRHMD || device is XRController)
                Debug.Log($"[XR] Device {state}: {device.displayName} ({device.layout})");
        }
    }
}
