using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UIElements;
using RenderingLayerMask = UnityEngine.RenderingLayerMask;
using UnityEngine.Rendering;

namespace UnityEditor.Rendering.HighDefinition
{
    /// <summary>
    /// A collection of utilities used by editor code of the HDRP.
    /// </summary>
    class HDEditorUtils
    {
        internal const string FormatingPath =
            @"Packages/com.unity.render-pipelines.high-definition/Editor/USS/Formating";

        internal const string QualitySettingsSheetPath =
            @"Packages/com.unity.render-pipelines.high-definition/Editor/USS/QualitySettings";

        internal const string WizardSheetPath =
            @"Packages/com.unity.render-pipelines.high-definition/Editor/USS/Wizard";

        internal const string HDRPAssetBuildLabel = "HDRP:IncludeInBuild";

        internal static bool NeedsToBeIncludedInBuild(HDRenderPipelineAsset hdRenderPipelineAsset)
        {
            var labelList = AssetDatabase.GetLabels(hdRenderPipelineAsset);
            foreach (string item in labelList)
            {
                if (item == HDUtils.k_HdrpAssetBuildLabel)
                {
                    return true;
                }
            }

            return false;
        }

        private static (StyleSheet baseSkin, StyleSheet professionalSkin, StyleSheet personalSkin) LoadStyleSheets(string basePath)
            => (
                AssetDatabase.LoadAssetAtPath<StyleSheet>($"{basePath}.uss"),
                AssetDatabase.LoadAssetAtPath<StyleSheet>($"{basePath}Light.uss"),
                AssetDatabase.LoadAssetAtPath<StyleSheet>($"{basePath}Dark.uss")
            );

        internal static void AddStyleSheets(VisualElement element, string baseSkinPath)
        {
            (StyleSheet @base, StyleSheet personal, StyleSheet professional) = LoadStyleSheets(baseSkinPath);
            element.styleSheets.Add(@base);
            if (EditorGUIUtility.isProSkin)
            {
                if (professional != null && !professional.Equals(null))
                    element.styleSheets.Add(professional);
            }
            else
            {
                if (personal != null && !personal.Equals(null))
                    element.styleSheets.Add(personal);
            }
        }

        static readonly Action<SerializedProperty, GUIContent> k_DefaultDrawer = (p, l) => EditorGUILayout.PropertyField(p, l);


        internal static T LoadAsset<T>(string relativePath) where T : UnityEngine.Object
            => AssetDatabase.LoadAssetAtPath<T>(HDUtils.GetHDRenderPipelinePath() + relativePath);

        /// <summary>
        /// Reset the dedicated Keyword and Pass regarding the shader kind.
        /// Also re-init the drawers and set the material dirty for the engine.
        /// </summary>
        /// <param name="material">The material that nees to be setup</param>
        /// <returns>
        /// True: managed to do the operation.
        /// False: unknown shader used in material
        /// </returns>
        [Obsolete("Use HDShaderUtils.ResetMaterialKeywords instead")]
        public static bool ResetMaterialKeywords(Material material)
            => HDShaderUtils.ResetMaterialKeywords(material);

        static readonly GUIContent s_OverrideTooltip = EditorGUIUtility.TrTextContent("", "Override this setting in component.");

        internal static bool FlagToggle<TEnum>(TEnum v, SerializedProperty property)
            where TEnum : struct, IConvertible // restrict to ~enum
        {
            var intV = (int)(object)v;
            var isOn = (property.intValue & intV) != 0;
            var rect = ReserveAndGetFlagToggleRect();
            isOn = GUI.Toggle(rect, isOn, s_OverrideTooltip, CoreEditorStyles.smallTickbox);
            if (isOn)
                property.intValue |= intV;
            else
                property.intValue &= ~intV;

            return isOn;
        }

        internal static Rect ReserveAndGetFlagToggleRect()
        {
            var rect = GUILayoutUtility.GetRect(11, 17, GUILayout.ExpandWidth(false));
            rect.y += 4;
            return rect;
        }

        internal static bool IsAssetPath(string path)
        {
            var isPathRooted = Path.IsPathRooted(path);
            return isPathRooted && path.StartsWith(Application.dataPath)
                   || !isPathRooted && path.StartsWith("Assets");
        }

        // Copy texture from cache
        internal static bool CopyFileWithRetryOnUnauthorizedAccess(string s, string path)
        {
            UnauthorizedAccessException exception = null;
            for (var k = 0; k < 20; ++k)
            {
                try
                {
                    File.Copy(s, path, true);
                    exception = null;
                }
                catch (UnauthorizedAccessException e)
                {
                    exception = e;
                }
            }

            if (exception != null)
            {
                Debug.LogException(exception);
                // Abort the update, something else is preventing the copy
                return false;
            }

            return true;
        }

        internal static void PropertyFieldWithoutToggle<TEnum>(
            TEnum v, SerializedProperty property, GUIContent label, TEnum displayed,
            Action<SerializedProperty, GUIContent> drawer = null, int indent = 0
        )
            where TEnum : struct, IConvertible // restrict to ~enum
        {
            var intDisplayed = (int)(object)displayed;
            var intV = (int)(object)v;
            if ((intDisplayed & intV) == intV)
            {
                EditorGUILayout.BeginHorizontal();

                var i = EditorGUI.indentLevel;
                EditorGUI.indentLevel = i + indent;
                (drawer ?? k_DefaultDrawer)(property, label);
                EditorGUI.indentLevel = i;

                EditorGUILayout.EndHorizontal();
            }
        }

        internal static void DrawToolBarButton<TEnum>(
            TEnum button, Editor owner,
            Dictionary<TEnum, EditMode.SceneViewEditMode> toolbarMode,
            Dictionary<TEnum, GUIContent> toolbarContent,
            params GUILayoutOption[] options
        )
            where TEnum : struct, IConvertible
        {
            var intButton = (int)(object)button;
            bool enabled = toolbarMode[button] == EditMode.editMode;
            EditorGUI.BeginChangeCheck();
            enabled = GUILayout.Toggle(enabled, toolbarContent[button], EditorStyles.miniButton, options);
            if (EditorGUI.EndChangeCheck())
            {
                EditMode.SceneViewEditMode targetMode = EditMode.editMode == toolbarMode[button] ? EditMode.SceneViewEditMode.None : toolbarMode[button];
                EditMode.ChangeEditMode(targetMode, GetBoundsGetter(owner)(), owner);
            }
        }

        internal static Func<Bounds> GetBoundsGetter(Editor o)
        {
            return () =>
            {
                var bounds = new Bounds();
                var rp = ((Component)o.target).transform;
                var b = rp.position;
                bounds.Encapsulate(b);
                return bounds;
            };
        }

        /// <summary>
        /// Give a human readable string representing the inputed weight given in byte.
        /// </summary>
        /// <param name="weightInByte">The weigth in byte</param>
        /// <returns>Human readable weight</returns>
        internal static string HumanizeWeight(long weightInByte)
        {
            if (weightInByte < 500)
            {
                return weightInByte + " B";
            }
            else if (weightInByte < 500000L)
            {
                float res = weightInByte / 1000f;
                return res.ToString("n2") + " KB";
            }
            else if (weightInByte < 500000000L)
            {
                float res = weightInByte / 1000000f;
                return res.ToString("n2") + " MB";
            }
            else
            {
                float res = weightInByte / 1000000000f;
                return res.ToString("n2") + " GB";
            }
        }

        /// <summary>
        /// Should be placed between BeginProperty / EndProperty
        /// </summary>
        internal static uint DrawRenderingLayerMask(Rect rect, uint renderingLayer, GUIContent label = null, bool allowHelpBox = true)
        {
            var value = EditorGUI.RenderingLayerMaskField(rect, label ?? GUIContent.none, renderingLayer);
            var definedLayersCount = RenderingLayerMask.GetLastDefinedRenderingLayerIndex();
            if (allowHelpBox && definedLayersCount > 16)
                EditorGUILayout.HelpBox($"One or more of the Rendering Layers is defined outside of 16 limit. HDRP supports only 16 layers.", MessageType.Warning);

            return value;
        }

        internal static void DrawRenderingLayerMask(Rect rect, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(rect, label, property);

            EditorGUI.BeginChangeCheck();
            var renderingLayer = DrawRenderingLayerMask(rect, property.uintValue, label);
            if (EditorGUI.EndChangeCheck())
            {
                if(property.numericType == SerializedPropertyNumericType.UInt32)
                    property.uintValue = renderingLayer;
                else
                    property.intValue = unchecked((int)renderingLayer);
            }

            EditorGUI.EndProperty();
        }

        internal static void DrawRenderingLayerMask(SerializedProperty property, GUIContent style)
        {
            Rect rect = EditorGUILayout.GetControlRect(true);
            DrawRenderingLayerMask(rect, property, style);
        }

        // IsPreset is an internal API - lets reuse the usable part of this function
        // 93 is a "magic number" and does not represent a combination of other flags here
        internal static bool IsPresetEditor(UnityEditor.Editor editor)
        {
            return (int)((editor.target as Component).gameObject.hideFlags) == 93;
        }

        internal static void QualitySettingsHelpBox(string message, MessageType type, HDRenderPipelineUI.ExpandableGroup uiGroupSection, string propertyPath)
        {
            CoreEditorUtils.DrawFixMeBox(message, type, "Open", () =>
            {
                SettingsService.OpenProjectSettings("Project/Quality/HDRP");
                HDRenderPipelineUI.Inspector.Expand((int)uiGroupSection);
                CoreEditorUtils.Highlight("Project Settings", propertyPath, HighlightSearchMode.Identifier);
                GUIUtility.ExitGUI();
            });
        }

        internal static void QualitySettingsHelpBox<TEnum>(string message, MessageType type, HDRenderPipelineUI.ExpandableGroup uiGroupSection, TEnum uiSection, string propertyPath)
            where TEnum : struct, IConvertible
        {
            QualitySettingsHelpBoxForReflection(message, type, uiGroupSection, uiSection.ToInt32(System.Globalization.CultureInfo.InvariantCulture), propertyPath);
        }

        internal static void QualitySettingsHelpBoxForReflection(string message, MessageType type, HDRenderPipelineUI.ExpandableGroup uiGroupSection, int uiSection, string propertyPath)
        {
            CoreEditorUtils.DrawFixMeBox(message, type, "Open", () =>
            {
                SettingsService.OpenProjectSettings("Project/Quality/HDRP");
                HDRenderPipelineUI.SubInspectors[uiGroupSection].Expand(uiSection);

                CoreEditorUtils.Highlight("Project Settings", propertyPath, HighlightSearchMode.Identifier);
                GUIUtility.ExitGUI();
            });
        }

        internal static void GlobalSettingsHelpBox(string message, MessageType type, string propertyPath)
        {
            CoreEditorUtils.DrawFixMeBox(message, type, "Open", () =>
            {
                SettingsService.OpenProjectSettings("Project/Graphics/HDRP Global Settings");
                CoreEditorUtils.Highlight("Project Settings", propertyPath);
                GUIUtility.ExitGUI();
            });
        }

        internal static void GlobalSettingsHelpBox(string message, MessageType type, FrameSettingsField field, string displayName)
        {
            CoreEditorUtils.DrawFixMeBox(message, type, "Open", () =>
            {
                var attribute = FrameSettingsExtractedDatas.GetFieldAttribute(field);

                SettingsService.OpenProjectSettings("Project/Graphics/HDRP Global Settings");
                FrameSettingsPropertyDrawer.SetExpended(FrameSettingsRenderType.Camera.ToString(), attribute.group, true);
                CoreEditorUtils.Highlight("Project Settings", displayName, HighlightSearchMode.Auto);
                GUIUtility.ExitGUI();
            });
        }

        static void OpenRenderingDebugger(string panelName)
        {
            var k_DebugWindowType = Type.GetType("UnityEditor.Rendering.DebugWindow,Unity.RenderPipelines.Core.Editor");
            var window = EditorWindow.GetWindow(k_DebugWindowType);
            window.titleContent = new GUIContent("Rendering Debugger");
            window.Show();

            if (panelName != null)
            {
                var manager = UnityEngine.Rendering.DebugManager.instance;
                manager.RequestEditorWindowPanelIndex(manager.FindPanelIndex(panelName));
            }
        }

        static void HighlightInDebugger(HDCamera hdCamera, FrameSettingsField field, string displayName)
        {
            OpenRenderingDebugger(hdCamera.camera.name);

            // Doesn't work for some reason
            //CoreEditorUtils.Highlight("Rendering Debugger", displayName, HighlightSearchMode.Auto);
            //GUIUtility.ExitGUI();
        }

        internal static void FrameSettingsHelpBox(HDCamera hdCamera, FrameSettingsField field, string displayName)
        {
            var data = HDUtils.TryGetAdditionalCameraDataOrDefault(hdCamera.camera);
            var defaults = GraphicsSettings.GetRenderPipelineSettings<RenderingPathFrameSettings>().GetDefaultFrameSettings(FrameSettingsRenderType.Camera);

            var type = MessageType.Warning;
            var attribute = FrameSettingsExtractedDatas.GetFieldAttribute(field);

            bool disabledInGlobal = !defaults.IsEnabled(field);
            bool disabledByCamera = data.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)field] &&
                                    !data.renderingPathCustomFrameSettings.IsEnabled(field);
            bool disabledByDependency = !attribute.dependencies.All(hdCamera.frameSettings.IsEnabled);

            var historyContainer = hdCamera.camera.cameraType == CameraType.SceneView
                ? FrameSettingsHistory.sceneViewFrameSettingsContainer
                : HDUtils.TryGetAdditionalCameraDataOrDefault(hdCamera.camera);
            bool disabledByDebug = FrameSettingsHistory.enabled && !historyContainer.frameSettingsHistory.debug.IsEnabled(field) && historyContainer.frameSettingsHistory.sanitazed.IsEnabled(field);

            var textBase = $"The FrameSetting required to render this effect in the {(hdCamera.camera.cameraType == CameraType.SceneView ? "Scene" : "Game")} view ";

            if (disabledByDebug)
                CoreEditorUtils.DrawFixMeBox(textBase + "is disabled in the Rendering Debugger.", type, "Open", () => HighlightInDebugger(hdCamera, field, displayName));
            else if (disabledByCamera)
                CoreEditorUtils.DrawFixMeBox(textBase + "is disabled on a Camera.", type, "Open", () => EditorUtility.OpenPropertyEditor(hdCamera.camera));
            else if (disabledInGlobal)
                GlobalSettingsHelpBox(textBase + "is disabled in the HDRP Global Settings.", type, field, displayName);
            else if (disabledByDependency)
                GlobalSettingsHelpBox(textBase + "depends on a disabled FrameSetting.", type, field, displayName);
        }

        internal static HDCamera[] GetDisplayedCameras()
        {
            HashSet<HDCamera> visibleCamera = new();

            foreach (SceneView sceneView in SceneView.sceneViews)
            {
                if (!sceneView.hasFocus) continue;
                visibleCamera.Add(HDCamera.GetOrCreate(sceneView.camera));
            }

            var assembly = typeof(EditorWindow).Assembly;
            var type = assembly.GetType("UnityEditor.GameView");
            var targetDisplayProp = type.GetProperty("targetDisplay");

            foreach (EditorWindow gameView in Resources.FindObjectsOfTypeAll(type))
            {
                if (!gameView.hasFocus) continue;

                var targetDisplay = (int)targetDisplayProp.GetValue(gameView);
                foreach (var camera in HDCamera.GetHDCameras())
                {
                    if (camera.camera.cameraType == CameraType.Game && camera.camera.targetDisplay == targetDisplay)
                        visibleCamera.Add(camera);
                }
            }

            return visibleCamera.ToArray();
        }

        internal static bool EnsureFrameSetting(FrameSettingsField field, string displayName)
        {
            foreach (var camera in GetDisplayedCameras())
            {
                if (!camera.frameSettings.IsEnabled(field))
                {
                    FrameSettingsHelpBox(camera, field, displayName);
                    EditorGUILayout.Space();
                    return false;
                }
            }

            return true;
        }

        internal static bool EnsureVolumeAndFrameSetting<T>(Func<T, string> volumeValidator, FrameSettingsField field, string displayName) where T : UnityEngine.Rendering.VolumeComponent
        {
            var cameras = GetDisplayedCameras();

            foreach (var camera in cameras)
            {
                var errorString = volumeValidator(camera.volumeStack.GetComponent<T>());
                if (!string.IsNullOrEmpty(errorString))
                {
                    EditorGUILayout.HelpBox(errorString, MessageType.Warning);
                    EditorGUILayout.Space();
                    return false;
                }
            }

            foreach (var camera in cameras)
            {
                if (!camera.frameSettings.IsEnabled(field))
                {
                    FrameSettingsHelpBox(camera, field, displayName);
                    EditorGUILayout.Space();
                    return false;
                }
            }

            return true;
        }
    }

    // Due to a UI bug/limitation, we have to do it this way to support bold labels
    internal class BoldLabelScope : GUI.Scope
    {
        FontStyle origFontStyle;

        public BoldLabelScope()
        {
            origFontStyle = EditorStyles.label.fontStyle;
            EditorStyles.label.fontStyle = FontStyle.Bold;
        }

        protected override void CloseScope()
        {
            EditorStyles.label.fontStyle = origFontStyle;
        }
    }
}
