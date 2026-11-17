using CesiumForUnity;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CityFeature.EditorTools
{
    /// <summary>
    /// One-click scene change: the third neighbourhood becomes Blaak (square by station Blaak / Markthal)
    /// instead of Laan op Zuid. Open CityScene, then B&D City > Replace Laan op Zuid with Blaak.
    /// </summary>
    public static class CitySpotsMenu
    {
        private const double BlaakLatitude = 51.91985;
        private const double BlaakLongitude = 4.48870;
        private const double SpotHeight = 45.0; // same hover height as the other spots

        [MenuItem("B&D City/Replace Laan op Zuid with Blaak")]
        private static void ReplaceWithBlaak()
        {
            GameObject spot = GameObject.Find("Spot - Laan op Zuid") ?? GameObject.Find("Spot - Blaak");
            if (spot == null) { EditorUtility.DisplayDialog("B&D City", "Open CityScene first (no 'Spot - Laan op Zuid' found).", "OK"); return; }

            Undo.RecordObject(spot, "Replace with Blaak");
            spot.name = "Spot - Blaak";
            var anchor = spot.GetComponent<CesiumGlobeAnchor>();
            Undo.RecordObject(anchor, "Replace with Blaak");
            anchor.longitudeLatitudeHeight = new double3(BlaakLongitude, BlaakLatitude, SpotHeight);

            var teleporter = Object.FindAnyObjectByType<NeighbourhoodTeleporter>();
            var so = new SerializedObject(teleporter);
            SerializedProperty list = so.FindProperty("neighbourhoods");
            for (int i = 0; i < list.arraySize; i++)
            {
                SerializedProperty entry = list.GetArrayElementAtIndex(i);
                if (entry.FindPropertyRelative("spot").objectReferenceValue == spot.transform)
                    entry.FindPropertyRelative("name").stringValue = "Blaak";
            }
            so.ApplyModifiedProperties();

            EditorSceneManager.MarkSceneDirty(spot.scene);
            EditorSceneManager.SaveScene(spot.scene);
            Debug.Log($"[City] Third neighbourhood is now Blaak at {spot.transform.position}.");
        }
    }
}
