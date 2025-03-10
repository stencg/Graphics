using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEditor.Rendering
{
    using CED = CoreEditorDrawer<SerializedProbeVolume>;

    static partial class ProbeVolumeUI
    {
        internal static readonly CED.IDrawer Inspector = CED.Group(
            CED.Group(
                Drawer_VolumeContent,
                Drawer_RebakeWarning // This needs to be last to avoid popping in the UI
            )
        );

        static void Drawer_BakeToolBar(SerializedProbeVolume serialized, Editor owner)
        {
            if (!ProbeReferenceVolume.instance.isInitialized) return;

            ProbeVolume pv = (serialized.serializedObject.targetObject as ProbeVolume);

            GIContributors.ContributorFilter? filter = null;

            if (GUILayout.Button(EditorGUIUtility.TrTextContent("Fit to All Scenes", "Fit this Adaptive Probe Volume to cover all loaded Scenes. "), EditorStyles.miniButton))
                filter = GIContributors.ContributorFilter.All;
            if (GUILayout.Button(EditorGUIUtility.TrTextContent("Fit to Scene", "Fit this Adaptive Probe Volume to the renderers in the same Scene."), EditorStyles.miniButton))
                filter = GIContributors.ContributorFilter.Scene;
            if (GUILayout.Button(EditorGUIUtility.TrTextContent("Fit to Selection", "Fits this Adaptive Probe Volume to the selected renderer(s). Lock the Inspector to make additional selections."), EditorStyles.miniButton))
                filter = GIContributors.ContributorFilter.Selection;

            if (filter.HasValue)
            {
                Undo.RecordObject(pv.transform, "Fitting Adaptive Probe Volume");

                // Get minBrickSize from scene profile if available
                float minBrickSize = ProbeReferenceVolume.instance.MinBrickSize();
                if (ProbeReferenceVolume.instance.TryGetBakingSetForLoadedScene(pv.gameObject.scene, out var profile))
                    minBrickSize = profile.minBrickSize;

                var bounds = pv.ComputeBounds(filter.Value, pv.gameObject.scene);
                pv.transform.position = bounds.center;
                serialized.size.vector3Value = Vector3.Max(bounds.size + new Vector3(minBrickSize, minBrickSize, minBrickSize), Vector3.zero);
            }
        }

        static int s_SubdivisionRangeID = "SubdivisionRange".GetHashCode();

        static void SubdivisionRange(SerializedProbeVolume serialized, int maxSubdiv, float minDistance)
        {
            var rect = EditorGUILayout.GetControlRect();
            EditorGUI.BeginProperty(rect, Styles.s_DistanceBetweenProbes, serialized.highestSubdivisionLevelOverride);
            EditorGUI.BeginProperty(rect, Styles.s_DistanceBetweenProbes, serialized.lowestSubdivisionLevelOverride);
            EditorGUI.BeginProperty(rect, Styles.s_DistanceBetweenProbes, serialized.overridesSubdivision);

            var checkbox = new Rect(rect) { width = 14 + 9, x = rect.x + 2 };
            serialized.overridesSubdivision.boolValue = EditorGUI.Toggle(checkbox, serialized.overridesSubdivision.boolValue);

            using (new EditorGUI.DisabledScope(!serialized.overridesSubdivision.boolValue))
            {
                EditorGUIUtility.labelWidth -= checkbox.width;
                rect.xMin = checkbox.xMax - 4;
                int id = GUIUtility.GetControlID(s_SubdivisionRangeID, FocusType.Keyboard, rect);
                rect = EditorGUI.PrefixLabel(rect, id, Styles.s_DistanceBetweenProbes);
                EditorGUIUtility.labelWidth += checkbox.width;

                if (serialized.highestSubdivisionLevelOverride.intValue > maxSubdiv)
                    serialized.highestSubdivisionLevelOverride.intValue = maxSubdiv;
                if (serialized.lowestSubdivisionLevelOverride.intValue > maxSubdiv)
                    serialized.lowestSubdivisionLevelOverride.intValue = maxSubdiv;

                float highest = maxSubdiv - serialized.highestSubdivisionLevelOverride.intValue;
                float lowest = maxSubdiv - serialized.lowestSubdivisionLevelOverride.intValue;
                EditorGUI.BeginChangeCheck();
                EditorGUI.MinMaxSlider(rect, ref highest, ref lowest, 0, maxSubdiv);
                if (EditorGUI.EndChangeCheck())
                {
                    GUIUtility.keyboardControl = id;
                    highest = maxSubdiv - Mathf.RoundToInt(highest);
                    lowest = Mathf.Min(maxSubdiv - Mathf.RoundToInt(lowest), highest);

                    serialized.highestSubdivisionLevelOverride.intValue = Mathf.RoundToInt(highest);
                    serialized.lowestSubdivisionLevelOverride.intValue = Mathf.RoundToInt(lowest);
                }

                ProbeVolumeLightingTab.DrawSimplificationLevelsMarkers(rect, minDistance, 0, maxSubdiv, (int)highest, (int)lowest);
            }

            EditorGUI.EndProperty();
            EditorGUI.EndProperty();
            EditorGUI.EndProperty();
        }

        static void Drawer_VolumeContent(SerializedProbeVolume serialized, Editor owner)
        {
            ProbeVolume pv = (serialized.serializedObject.targetObject as ProbeVolume);

            ProbeReferenceVolume.instance.TryGetBakingSetForLoadedScene(pv.gameObject.scene, out var bakingSet);
            if (bakingSet == null)
                bakingSet = ProbeVolumeLightingTab.GetSingleSceneSet(pv.gameObject.scene);

            EditorGUILayout.PropertyField(serialized.mode);
            if (serialized.mode.intValue == (int)ProbeVolume.Mode.Local)
            {
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(serialized.size, Styles.s_Size);
                if (EditorGUI.EndChangeCheck())
                    serialized.size.vector3Value = Vector3.Max(serialized.size.vector3Value, Vector3.zero);

                Drawer_BakeToolBar(serialized, owner);
            }

            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Subdivision Override", EditorStyles.boldLabel);
            bool isFreezingPlacement = bakingSet != null && bakingSet.freezePlacement && ProbeGIBaking.CanFreezePlacement();
            using (new EditorGUI.DisabledScope(isFreezingPlacement))
            {
                // Get settings from scene profile if available
                int maxSubdiv = ProbeReferenceVolume.instance.GetMaxSubdivision() - 1;
                float minDistance = ProbeReferenceVolume.instance.MinDistanceBetweenProbes();
                if (bakingSet != null)
                {
                    maxSubdiv = bakingSet.simplificationLevels;
                    minDistance = bakingSet.minDistanceBetweenProbes;
                }
                if (maxSubdiv == -1)
                {
                    maxSubdiv = 5;
                    minDistance = 1;
                }
                maxSubdiv = Mathf.Max(0, maxSubdiv);

                SubdivisionRange(serialized, maxSubdiv, minDistance);
            }

            if (isFreezingPlacement)
            {
                CoreEditorUtils.DrawFixMeBox("The placement is frozen in the baking settings. To change these values uncheck the Freeze Placement in the Adaptive Probe Volumes tab of the Lighting Window.", MessageType.Info, "Open", () =>
                {
                    ProbeVolumeLightingTab.OpenBakingSet(bakingSet);
                });
            }

            EditorGUILayout.LabelField("Geometry Settings", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(serialized.overrideRendererFilters, Styles.s_OverrideRendererFilters);
            if (serialized.overrideRendererFilters.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serialized.objectLayerMask, Styles.s_ObjectLayerMask);
                EditorGUILayout.PropertyField(serialized.minRendererVolumeSize, Styles.s_MinRendererVolumeSize);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.PropertyField(serialized.fillEmptySpaces);

            if (bakingSet == null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("The scene this Adaptive Probe Volume is part of does not belong to any Baking Set.", MessageType.Warning);
            }
        }

        static void Drawer_RebakeWarning(SerializedProbeVolume serialized, Editor owner)
        {
            ProbeVolume pv = (serialized.serializedObject.targetObject as ProbeVolume);

            if (pv.mightNeedRebaking)
            {
                EditorGUILayout.Space();
                var helpBoxRect = GUILayoutUtility.GetRect(new GUIContent(Styles.s_ProbeVolumeChangedMessage, EditorGUIUtility.IconContent("Warning@2x").image), EditorStyles.helpBox);
                EditorGUI.HelpBox(helpBoxRect, Styles.s_ProbeVolumeChangedMessage, MessageType.Warning);
            }
        }
    }
}
