using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Impostors.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Impostors.Editor
{
    /// <summary>
    /// Measures native draw calls independently of the commands emitted by BRG culling.
    /// Runs in an isolated preview scene and does not change the active render pipeline.
    /// </summary>
    internal static class ImpostorBrgValidation
    {
        private const string THE_VALIDATION_REQUIRES_THE_PROJECT_S_ACTIVE_SRP = "The validation requires the project's active SRP.";
        private const string DISABLE_THE_CURRENT_FRAME_DEBUGGER_CAPTURE_BEFORE_RUNNING = "Disable the current Frame Debugger capture before running this isolated validation. Its capture is preserved.";
        private const string BRG_REGRESSION_CAMERA = "BRG regression camera";
        private const string BRG_VALIDATION_TARGET = "BRG validation target";
        private const string UNITY_STATS_COUNTERS_ARE_EDITOR_FRAME_DIAGNOSTICS_ONLY_DIRECT = "UnityStats counters are editor-frame diagnostics only. Direct Camera.Render does not refresh them; acceptance uses the isolated Frame Debugger events.";
        private const string DATASET_REPLACEMENT_LEFT_STALE_BRG_REGISTRATIONS_OR_VISIBILITY = "Dataset replacement left stale BRG registrations or visibility.";
        private const string CLEARING_A_DATASET_RETAINED_VISIBILITY_COMMANDS_OR_GPU = "Clearing a dataset retained visibility, commands or GPU buffers.";
        private const string REPLACEMENT_NATIVE_DRAWS_INSTANCES_CLEARED_DRAWS_INSTANCES = "replacement {0}: {1} native draws / {2} instances; cleared: {3} draws / {4} instances";
        private const string RELEASING_SOURCE_MESHES_INVALIDATED_A_REGISTERED_RUNTIME_PROXY = "Releasing source meshes invalidated a registered runtime proxy.";
        private const string UNUSED_ASSET_UNLOADING_INVALIDATED_RUNTIME_MESH_OR_MATERIAL = "Unused-asset unloading invalidated runtime mesh or material registrations.";
        private const string BRG_REGRESSION_HOST = "BRG regression world";
        private const string RE_ENABLING_A_FOREST_HOST_DID_NOT_RESTORE = "A recreated rendering world did not restore valid rendering.";
        private const string DISABLING_A_HOST_DID_NOT_DISPOSE_ITS_RENDERER = "Disposing a world did not dispose its renderer.";
        private const string UNITY_EDITOR_INTERNAL_FRAME_DEBUGGER_INTERNAL_FRAME_DEBUGGER_UTILITY = "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility";
        private const string UNITY_EDITOR_INTERNAL_FRAME_DEBUGGER_INTERNAL_FRAME_DEBUGGER_EVENT_DATA = "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData";
        private const string THIS_UNITY_VERSION_DOES_NOT_EXPOSE_THE_EXPECTED = "This Unity version does not expose the expected Frame Debugger capture API.";
        private const string ENTER_CAPTURING_SCOPE = "EnterCapturingScope";
        private const string LEAVE_CAPTURING_SCOPE = "LeaveCapturingScope";
        private const string SET_ENABLED = "SetEnabled";
        private const string GET_FRAME_EVENTS = "GetFrameEvents";
        private const string GET_FRAME_EVENT_DATA = "GetFrameEventData";
        private const string LIMIT = "limit";
        private const string THE_FRAME_DEBUGGER_CAPTURE_API_IS_INCOMPLETE = "The Frame Debugger capture API is incomplete.";
        private const string GET_REMOTE_PLAYER_GUID = "GetRemotePlayerGUID";
        private const string COUNT = "count";
        private const string LOCALLY_SUPPORTED = "locallySupported";
        private const string NATIVE_CAPTURE_UNSUPPORTED = "Local Frame Debugger capture is unsupported for {0} / {1}. Run native draw validation in a supported editor graphics mode.";
        private const string ENABLED_LIMIT_NATIVE_COUNT = "enabled={0}, limit={1}, nativeCount={2}";
        private const string GET_FRAME_EVENT_INFO_NAME = "GetFrameEventInfoName";
        private const string M_TYPE = "m_Type";
        private const string M_REAL_SHADER_NAME = "m_RealShaderName";
        private const string M_PASS_NAME = "m_PassName";
        private const string M_DRAW_CALL_COUNT = "m_DrawCallCount";
        private const string M_INSTANCE_COUNT = "m_InstanceCount";
        private const string DATA_SHADER_PASS_DRAWS_INSTANCES = "{0}: {1} {2}, data={3}, shader={4}, pass={5}, draws={6}, instances={7}";
        private const string FRAME_DEBUGGER_NATIVE_FORWARD_LIT_EVENT_DATA = "Frame Debugger native ForwardLit event data";
        private const string NO_NATIVE_FORWARD_EVENT_DATA_WAS_AVAILABLE_SUBMITTED = "No native forward-event data was available. Submitted command counts are not a substitute for this measurement.";

        [Serializable]
        public sealed class DrawEvent
        {
            public string Name;
            public string Pass;
            public int DrawCalls;
            public int Instances;
        }

        [Serializable]
        public sealed class Report
        {
            public int Placements;
            public bool CameraFacingProxy;
            public int SubmittedInstances;
            public int SubmittedCommands;
            public int NativeForwardDrawCalls;
            public int NativeForwardInstances;
            public int UnityStatsHybridDrawCalls;
            public int UnityStatsHybridInstances;
            public bool NativeMeasurementAvailable;
            public string NativeMeasurementSource;
            public bool NativeCounterIsolationPassed = true;
            public bool Batched;
            public bool DatasetReplacementPassed;
            public bool SourceMeshReleasePassed;
            public bool UnusedAssetUnloadPassed;
            public bool RepeatedHostEnablePassed;
            public string MeasurementError;
            public int CapturedEventCount;
            public List<string> CaptureDiagnostics = new List<string>();
            public List<DrawEvent> Events = new List<DrawEvent>();
        }

        public static async Task<Report> RunAsync(int placementCount = 4096, bool replaceExistingDebuggerCapture = false)
        {
            if (placementCount < 16 || placementCount > 100000)
            {
                throw new ArgumentOutOfRangeException(nameof(placementCount));
            }

            if (!GraphicsSettings.currentRenderPipeline)
            {
                throw new InvalidOperationException(THE_VALIDATION_REQUIRES_THE_PROJECT_S_ACTIVE_SRP);
            }

            if (FrameDebugger.enabled && !replaceExistingDebuggerCapture)
            {
                throw new InvalidOperationException(DISABLE_THE_CURRENT_FRAME_DEBUGGER_CAPTURE_BEFORE_RUNNING);
            }

            var definitions = new ImpostorDefinition[2];
            var sourceProxies = new Mesh[2];
            ImpostorRenderData data = null;
            ImpostorRenderData smaller = null;
            ImpostorWorldRenderer renderer = null;
            RenderTexture target = null;
            Scene scene = EditorSceneManager.NewPreviewScene();
            var report = new Report
            {
                Placements = placementCount,
                CameraFacingProxy = true
            };
            try
            {
                for (int prototype = 0; prototype < definitions.Length; prototype++)
                {
                    ImpostorDefinition source = Tests.ImpostorTestAssets.Definition(prototype + 1);
                    if (!source || !source.IsValid)
                    {
                        throw new InvalidOperationException("A complete portable definition is required.");
                    }

                    definitions[prototype] = Object.Instantiate(source);
                    definitions[prototype].hideFlags = HideFlags.HideAndDontSave;
                    sourceProxies[prototype] = Object.Instantiate(source.Proxy);
                    definitions[prototype].Proxy = sourceProxies[prototype];
                }

                data = CreateData(definitions, placementCount);
                smaller = CreateData(definitions, 16);
                ulong sceneMask = EditorSceneManager.GetSceneCullingMask(scene);
                renderer = new ImpostorWorldRenderer
                {
                    SceneMask = sceneMask
                };
                renderer.SetData(data);
                var cameraObject = new GameObject(BRG_REGRESSION_CAMERA, typeof(Camera));
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                Camera camera = cameraObject.GetComponent<Camera>();
                camera.enabled = false;
                camera.scene = scene;
                camera.overrideSceneCullingMask = sceneMask;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.orthographic = true;
                camera.aspect = 16f / 9;
                int columns = Mathf.CeilToInt(Mathf.Sqrt(placementCount * camera.aspect));
                int rows = Mathf.CeilToInt(placementCount / (float)columns);
                camera.orthographicSize = Mathf.Max(rows * 4 + 24, (columns * 4 + 24) / camera.aspect);
                camera.transform.SetPositionAndRotation(new Vector3(0, 100, 0), Quaternion.Euler(90, 0, 0));
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 300;
                camera.allowHDR = false;
                camera.allowMSAA = true;
                target = new RenderTexture(1024, 576, 24, RenderTextureFormat.ARGB32)
                {
                    name = BRG_VALIDATION_TARGET,
                    antiAliasing = 4,
                    hideFlags = HideFlags.HideAndDontSave
                };
                target.Create();
                camera.targetTexture = target;
                // Warm shader variants before the native draw measurement.
                for (int frame = 0; frame < 3; frame++)
                {
                    camera.Render();
                    await Task.Yield();
                }

                report.SubmittedInstances = renderer.SubmittedCount;
                report.SubmittedCommands = renderer.DrawCommandCount;
                report.UnityStatsHybridDrawCalls = UnityStats.hybridBatcherDrawCalls;
                report.UnityStatsHybridInstances = UnityStats.hybridBatcherInstances;
                report.CaptureDiagnostics.Add(UNITY_STATS_COUNTERS_ARE_EDITOR_FRAME_DIAGNOSTICS_ONLY_DIRECT);
                CaptureNativeDraws(camera, report);
                for (int iteration = 0; iteration < 3; iteration++)
                {
                    renderer.SetData(smaller);
                    camera.Render();
                    if (renderer.SubmittedCount != smaller.Instances.Length || !renderer.HasValidRegistrations)
                    {
                        throw new InvalidOperationException(DATASET_REPLACEMENT_LEFT_STALE_BRG_REGISTRATIONS_OR_VISIBILITY);
                    }

                    var smallCapture = new Report();
                    CaptureNativeDraws(camera, smallCapture);
                    report.NativeCounterIsolationPassed &= smallCapture.NativeMeasurementAvailable && smallCapture.NativeForwardInstances == smaller.Instances.Length;
                    renderer.SetData(null);
                    camera.Render();
                    if (renderer.InstanceCount != 0 || renderer.SubmittedCount != 0 || renderer.DrawCommandCount != 0 || renderer.InstanceBufferBytes != 0)
                    {
                        throw new InvalidOperationException(CLEARING_A_DATASET_RETAINED_VISIBILITY_COMMANDS_OR_GPU);
                    }

                    var emptyCapture = new Report();
                    CaptureNativeDraws(camera, emptyCapture);
                    report.NativeCounterIsolationPassed &= emptyCapture.CapturedEventCount > 0 && emptyCapture.NativeForwardDrawCalls == 0 && emptyCapture.NativeForwardInstances == 0;
                    report.CaptureDiagnostics.Add(string.Format(REPLACEMENT_NATIVE_DRAWS_INSTANCES_CLEARED_DRAWS_INSTANCES, iteration, smallCapture.NativeForwardDrawCalls, smallCapture.NativeForwardInstances, emptyCapture.NativeForwardDrawCalls, emptyCapture.NativeForwardInstances));
                }

                report.Batched = report.NativeMeasurementAvailable && report.NativeCounterIsolationPassed && report.SubmittedInstances == placementCount && report.NativeForwardInstances == placementCount && report.NativeForwardDrawCalls > 0 && report.NativeForwardDrawCalls < placementCount / 16;
                renderer.SetData(data);
                report.DatasetReplacementPassed = true;
                // The renderer must own the registered proxy, rather than relying on the source asset's lifetime.
                for (int prototype = 0; prototype < definitions.Length; prototype++)
                {
                    definitions[prototype].Proxy = null;
                    Object.DestroyImmediate(sourceProxies[prototype]);
                    sourceProxies[prototype] = null;
                }

                report.SourceMeshReleasePassed = renderer.HasValidRegistrations;
                if (!report.SourceMeshReleasePassed)
                {
                    throw new InvalidOperationException(RELEASING_SOURCE_MESHES_INVALIDATED_A_REGISTERED_RUNTIME_PROXY);
                }

                AsyncOperation unload = Resources.UnloadUnusedAssets();
                while (!unload.isDone)
                {
                    await Task.Yield();
                }

                report.UnusedAssetUnloadPassed = renderer.HasValidRegistrations;
                if (!report.UnusedAssetUnloadPassed)
                {
                    throw new InvalidOperationException(UNUSED_ASSET_UNLOADING_INVALIDATED_RUNTIME_MESH_OR_MATERIAL);
                }

                camera.Render();
                report.UnusedAssetUnloadPassed &= renderer.SubmittedCount == placementCount;
                // Repeated host enable/disable follows the same public lifecycle used by an editing scene.
                renderer.Dispose();
                renderer.Dispose();
                renderer = null;
                ImpostorDefinition[] persistentDefinitions = new ImpostorDefinition[2];
                for (int prototype = 0; prototype < persistentDefinitions.Length; prototype++)
                {
                    persistentDefinitions[prototype] = Tests.ImpostorTestAssets.Definition(prototype + 1);
                }

                // Rendering is owned by the ECS system: each world builds its renderers and releases them on disposal.
                for (int iteration = 0; iteration < 3; iteration++)
                {
                    ImpostorWorldRenderer owned;
                    using (var world = new Unity.Entities.World(BRG_REGRESSION_HOST))
                    {
                        var system = world.CreateSystemManaged<ImpostorRenderingSystem>();
                        foreach (ImpostorInstance placement in smaller.Instances)
                        {
                            Unity.Entities.Entity prefab = system.CreatePrefab(persistentDefinitions[placement.Prototype]);
                            var transforms = new Unity.Collections.NativeArray<Unity.Mathematics.float4x4>(1, Unity.Collections.Allocator.Temp);
                            transforms[0] = placement.Matrix;
                            system.Instantiate(prefab, transforms, Unity.Collections.Allocator.Temp);
                        }

                        system.Update();
                        owned = system.GetRenderer(0, false);
                        if (owned == null)
                        {
                            throw new InvalidOperationException(RE_ENABLING_A_FOREST_HOST_DID_NOT_RESTORE);
                        }

                        owned.SceneMask = sceneMask;
                        owned.SynchronizeEntities(true);
                        camera.Render();
                        if (!owned.HasValidRegistrations || owned.SubmittedCount != smaller.Instances.Length)
                        {
                            throw new InvalidOperationException(RE_ENABLING_A_FOREST_HOST_DID_NOT_RESTORE);
                        }
                    }

                    if (owned.HasValidRegistrations)
                    {
                        throw new InvalidOperationException(DISABLING_A_HOST_DID_NOT_DISPOSE_ITS_RENDERER);
                    }
                }

                report.RepeatedHostEnablePassed = true;
                return report;
            }
            finally
            {
                renderer?.Dispose();
                EditorSceneManager.ClosePreviewScene(scene);
                if (target)
                {
                    target.Release();
                    Object.DestroyImmediate(target);
                }

                foreach (Mesh mesh in sourceProxies)
                {
                    if (mesh)
                    {
                        Object.DestroyImmediate(mesh);
                    }
                }

                foreach (ImpostorDefinition definition in definitions)
                {
                    if (definition)
                    {
                        Object.DestroyImmediate(definition);
                    }
                }
            }
        }

        private static ImpostorRenderData CreateData(ImpostorDefinition[] definitions, int count)
        {
            var data = new ImpostorRenderData(definitions, new ImpostorInstance[count]);
            int columns = Mathf.CeilToInt(Mathf.Sqrt(count * 16f / 9));
            int rows = Mathf.CeilToInt(count / (float)columns);
            for (int index = 0; index < count; index++)
            {
                data.Instances[index] = new ImpostorInstance
                {
                    Prototype = index % 2,
                    Position = new Vector3((index % columns - (columns - 1) * 0.5f) * 8, 0, (index / columns - (rows - 1) * 0.5f) * 8),
                    Yaw = index * 137.50776f % 360,
                    Scale = 1,
                };
            }

            return data;
        }

        private static void CaptureNativeDraws(Camera camera, Report report)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type utility = typeof(UnityEditor.Editor).Assembly.GetType(UNITY_EDITOR_INTERNAL_FRAME_DEBUGGER_INTERNAL_FRAME_DEBUGGER_UTILITY);
            Type eventDataType = typeof(UnityEditor.Editor).Assembly.GetType(UNITY_EDITOR_INTERNAL_FRAME_DEBUGGER_INTERNAL_FRAME_DEBUGGER_EVENT_DATA);
            if (utility == null || eventDataType == null)
            {
                report.MeasurementError = THIS_UNITY_VERSION_DOES_NOT_EXPOSE_THE_EXPECTED;
                return;
            }

            PropertyInfo supported = utility.GetProperty(LOCALLY_SUPPORTED, flags);
            if (supported != null && !(bool)supported.GetValue(null))
            {
                report.MeasurementError = string.Format(NATIVE_CAPTURE_UNSUPPORTED,
                    SystemInfo.graphicsDeviceType, SystemInfo.renderingThreadingMode);
                return;
            }

            MethodInfo enter = utility.GetMethod(ENTER_CAPTURING_SCOPE, flags);
            MethodInfo leave = utility.GetMethod(LEAVE_CAPTURING_SCOPE, flags);
            MethodInfo enable = utility.GetMethod(SET_ENABLED, flags);
            MethodInfo getEvents = utility.GetMethod(GET_FRAME_EVENTS, flags);
            MethodInfo getData = utility.GetMethod(GET_FRAME_EVENT_DATA, flags);
            PropertyInfo limit = utility.GetProperty(LIMIT, flags);
            if (enter == null || leave == null || enable == null || getEvents == null || getData == null || limit == null)
            {
                report.MeasurementError = THE_FRAME_DEBUGGER_CAPTURE_API_IS_INCOMPLETE;
                return;
            }

            int previousLimit = (int)limit.GetValue(null);
            bool previousEnabled = FrameDebugger.enabled;
            int previousPlayer = (int)utility.GetMethod(GET_REMOTE_PLAYER_GUID, flags).Invoke(null, null);
            try
            {
                if (previousEnabled)
                {
                    enable.Invoke(null, new object[] { false, previousPlayer });
                }

                // The editor connection is -1 in this Unity version; zero requests an invalid remote player.
                enable.Invoke(null, new object[] { true, UnityEditorInternal.ProfilerDriver.connectedProfiler });
                limit.SetValue(null, int.MaxValue);
                enter.Invoke(null, null);
                try
                {
                    camera.Render();
                }
                finally
                {
                    leave.Invoke(null, null);
                }

                AsyncGPUReadback.Request(camera.targetTexture).WaitForCompletion();
                Array events = (Array)getEvents.Invoke(null, null);
                report.CapturedEventCount = events.Length;
                report.CaptureDiagnostics.Add(string.Format(ENABLED_LIMIT_NATIVE_COUNT, FrameDebugger.enabled, limit.GetValue(null), utility.GetProperty(COUNT, flags).GetValue(null)));
                for (int index = 0; index < events.Length; index++)
                {
                    limit.SetValue(null, index + 1);
                    enter.Invoke(null, null);
                    try
                    {
                        camera.Render();
                    }
                    finally
                    {
                        leave.Invoke(null, null);
                    }

                    AsyncGPUReadback.Request(camera.targetTexture).WaitForCompletion();
                    object eventData = Activator.CreateInstance(eventDataType, true);
                    bool available = (bool)getData.Invoke(null, new[] { (object)index, eventData });
                    string name = utility.GetMethod(GET_FRAME_EVENT_INFO_NAME, flags).Invoke(null, new object[] { index }) as string;
                    report.CaptureDiagnostics.Add(string.Format(DATA_SHADER_PASS_DRAWS_INSTANCES, index, events.GetValue(index).GetType().GetField(M_TYPE).GetValue(events.GetValue(index)), name, available, eventDataType.GetField(M_REAL_SHADER_NAME).GetValue(eventData), eventDataType.GetField(M_PASS_NAME).GetValue(eventData), eventDataType.GetField(M_DRAW_CALL_COUNT).GetValue(eventData), eventDataType.GetField(M_INSTANCE_COUNT).GetValue(eventData)));
                    if (!available)
                    {
                        continue;
                    }

                    string shader = (string)eventDataType.GetField(M_REAL_SHADER_NAME).GetValue(eventData);
                    string pass = (string)eventDataType.GetField(M_PASS_NAME).GetValue(eventData);
                    if (shader != ImpostorShaderNames.HEMI_OCTAHEDRAL || pass != ImpostorShaderPasses.FORWARD_LIT)
                    {
                        continue;
                    }

                    int draws = (int)eventDataType.GetField(M_DRAW_CALL_COUNT).GetValue(eventData);
                    int instances = (int)eventDataType.GetField(M_INSTANCE_COUNT).GetValue(eventData);
                    report.Events.Add(new DrawEvent { Name = utility.GetMethod(GET_FRAME_EVENT_INFO_NAME, flags).Invoke(null, new object[] { index }) as string, Pass = pass, DrawCalls = draws, Instances = instances });
                    report.NativeForwardDrawCalls += draws;
                    report.NativeForwardInstances += instances;
                }

                report.NativeMeasurementAvailable = report.Events.Count > 0;
                if (report.NativeMeasurementAvailable)
                {
                    report.NativeMeasurementSource = FRAME_DEBUGGER_NATIVE_FORWARD_LIT_EVENT_DATA;
                }

                if (!report.NativeMeasurementAvailable)
                {
                    report.MeasurementError = NO_NATIVE_FORWARD_EVENT_DATA_WAS_AVAILABLE_SUBMITTED;
                }
            }
            catch (Exception exception)
            {
                report.MeasurementError = exception.GetBaseException().Message;
            }
            finally
            {
                enable.Invoke(null, new object[] { previousEnabled, previousPlayer });
                limit.SetValue(null, previousLimit);
            }
        }
    }
}
