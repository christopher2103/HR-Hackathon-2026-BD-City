using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace CityFeature.EditorTools
{
    /// <summary>
    /// Restyles the GiftMenu prefab as a "soap bubble": frosted rounded panel with a soft gloss, a rainbow rim
    /// (animated by MenuBubbleStyle), pill-shaped buttons that light up warm orange, dark navy text.
    /// Only changes looks (sprites, colours, fonts); the menu logic and layout stay the same. Re-runnable.
    /// </summary>
    public static class CityMenuStyle
    {
        const string PrefabPath = "Assets/BubbleFeature/Prefabs/GiftMenu.prefab";
        const string Tex = "Assets/BubbleFeature/Textures/";
        static readonly Color Panel = new Color(0.93f, 0.96f, 1.00f, 0.88f);
        static readonly Color Navy = new Color(0.10f, 0.13f, 0.24f);
        static readonly Color Orange = new Color(0.95f, 0.55f, 0.25f);

        [MenuItem("B&D City/Menu/Apply bubble style")]
        public static string Apply()
        {
            Sprite panel = Import("UI_BubblePanel", new Vector4(72, 72, 72, 72));
            Sprite rimS = Import("UI_BubbleRim", new Vector4(72, 72, 72, 72));
            Sprite pill = Import("UI_Pill", new Vector4(31, 31, 31, 31));
            Sprite gloss = Import("UI_Gloss", Vector4.zero);
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/TextMesh Pro/Examples & Extras/Resources/Fonts & Materials/Roboto-Bold SDF.asset");

            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            var canvas = root.transform.Find("MenuCanvas");
            var img = canvas.GetComponent<Image>();
            img.sprite = panel; img.type = Image.Type.Sliced; img.color = Panel; img.pixelsPerUnitMultiplier = 1f;

            // Gloss (top highlight) and rim, drawn on top of the panel but under the content.
            Image Layer(string name, Sprite s, bool sliced, int index)
            {
                Transform t = canvas.Find(name);
                GameObject go = t != null ? t.gameObject : new GameObject(name, typeof(RectTransform), typeof(Image));
                go.transform.SetParent(canvas, false);
                go.transform.SetSiblingIndex(index);
                var im = go.GetComponent<Image>();
                im.sprite = s; im.type = sliced ? Image.Type.Sliced : Image.Type.Simple; im.raycastTarget = false;
                return im;
            }
            var glossImg = Layer("Gloss", gloss, false, 0);
            var grt = (RectTransform)glossImg.transform;
            grt.anchorMin = grt.anchorMax = new Vector2(0.5f, 1f); grt.pivot = new Vector2(0.5f, 1f);
            grt.sizeDelta = new Vector2(480f, 110f); grt.anchoredPosition = new Vector2(-30f, -10f);
            glossImg.color = new Color(1f, 1f, 1f, 0.75f);
            var rimImg = Layer("Rim", rimS, true, 1);
            var rrt = (RectTransform)rimImg.transform;
            rrt.anchorMin = Vector2.zero; rrt.anchorMax = Vector2.one; rrt.offsetMin = new Vector2(-6, -6); rrt.offsetMax = new Vector2(6, 6);

            var style = canvas.GetComponent<BubbleFeature.Menu.MenuBubbleStyle>();
            if (style == null) style = canvas.gameObject.AddComponent<BubbleFeature.Menu.MenuBubbleStyle>();
            var so = new SerializedObject(style); so.FindProperty("rim").objectReferenceValue = rimImg; so.ApplyModifiedPropertiesWithoutUndo();

            // Title.
            var title = canvas.Find("Title").GetComponent<TMP_Text>();
            if (font != null) title.font = font;
            title.color = Navy; title.fontSize = 40; title.characterSpacing = 1f;

            // Buttons (the template every option is cloned from, and Back).
            foreach (var path in new[] { "Options/OptionTemplate", "BackButton" })
            {
                var b = canvas.Find(path);
                var bimg = b.GetComponent<Image>();
                bimg.sprite = pill; bimg.type = Image.Type.Sliced; bimg.color = Color.white;
                var btn = b.GetComponent<Button>();
                var cb = btn.colors;
                cb.normalColor = new Color(1f, 1f, 1f, 0.92f);
                cb.highlightedColor = new Color(1f, 0.80f, 0.62f, 1f);
                cb.pressedColor = Orange;
                cb.selectedColor = new Color(1f, 1f, 1f, 0.92f);
                cb.disabledColor = new Color(1f, 1f, 1f, 0.45f);
                cb.colorMultiplier = 1f; cb.fadeDuration = 0.08f;
                btn.colors = cb;
                var t = b.GetComponentInChildren<TMP_Text>();
                if (font != null) t.font = font;
                t.color = Navy;
                t.fontSize = path == "BackButton" ? 28 : 28;
                if (path == "BackButton") t.text = "< Back";
            }
            var layout = canvas.Find("Options").GetComponent<VerticalLayoutGroup>();
            layout.spacing = 12;

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            AssetDatabase.SaveAssets();
            return "Menu restyled";
        }

        static Sprite Import(string name, Vector4 border)
        {
            string path = Tex + name + ".png";
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.alphaIsTransparency = true; ti.mipmapEnabled = false; ti.wrapMode = TextureWrapMode.Clamp;
            ti.spriteBorder = border; ti.spritePixelsPerUnit = 100;
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }
    }
}
