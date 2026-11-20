using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Impostors.Editor
{
    /// <summary>Records isolated capture camera state and its restoration in the same command buffer.</summary>
    internal sealed class ImpostorCaptureState : IDisposable
    {
        private static readonly string[] DisabledKeywords =
        {
            "FOG_LINEAR", "FOG_EXP", "FOG_EXP2", "DEBUG_DISPLAY",
            "_DBUFFER_MRT1", "_DBUFFER_MRT2", "_DBUFFER_MRT3",
            "_SCREEN_SPACE_OCCLUSION", "_SCREEN_SPACE_REFLECTION", "_SCREEN_SPACE_IRRADIANCE",
            "_WRITE_RENDERING_LAYERS", "LIGHTMAP_ON", "DYNAMICLIGHTMAP_ON",
            "LOD_FADE_CROSSFADE", "DOTS_INSTANCING_ON", "_CLUSTER_LIGHT_LOOP",
            "_ADDITIONAL_LIGHTS", "_ADDITIONAL_LIGHTS_VERTEX", "_MAIN_LIGHT_SHADOWS_SCREEN",
        };

        private static readonly string[] MatrixNames =
        {
            "unity_MatrixV", "glstate_matrix_projection", "unity_MatrixVP",
            "unity_MatrixInvV", "unity_MatrixInvP", "unity_MatrixInvVP",
        };

        private static readonly string[] VectorNames =
        {
            "_WorldSpaceCameraPos", "_ProjectionParams", "_ScreenParams", "_ScaledScreenParams",
            "unity_OrthoParams", "_ZBufferParams", "_Time", "_TimeParameters", "unity_DeltaTime", "_SinTime", "_CosTime",
        };

        private readonly CommandBuffer _commands;
        private readonly bool[] _keywords = new bool[DisabledKeywords.Length];
        private readonly Matrix4x4[] _matrices = new Matrix4x4[MatrixNames.Length];
        private readonly Vector4[] _vectors = new Vector4[VectorNames.Length];

        public ImpostorCaptureState(CommandBuffer commands, Matrix4x4 view, Matrix4x4 projection, Matrix4x4 cameraProjection, int size)
        {
            _commands = commands;
            for (int i = 0; i < DisabledKeywords.Length; i++)
            {
                _keywords[i] = Shader.IsKeywordEnabled(DisabledKeywords[i]);
                commands.DisableShaderKeyword(DisabledKeywords[i]);
            }

            for (int i = 0; i < MatrixNames.Length; i++)
            {
                _matrices[i] = Shader.GetGlobalMatrix(MatrixNames[i]);
            }

            for (int i = 0; i < VectorNames.Length; i++)
            {
                _vectors[i] = Shader.GetGlobalVector(VectorNames[i]);
            }

            // Also set native draw state: global matrices alone leave the previous
            // camera's winding state active on render-texture draws.
            commands.SetViewProjectionMatrices(view, cameraProjection);
            RenderingUtils.SetViewAndProjectionMatrices(commands, view, projection, true);
            commands.SetGlobalVector("_WorldSpaceCameraPos", view.inverse.GetColumn(3));
            float near = (cameraProjection.m23 + 1) / cameraProjection.m22;
            float far = (cameraProjection.m23 - 1) / cameraProjection.m22;
            commands.SetGlobalVector("_ProjectionParams", new Vector4(Mathf.Sign(projection.m11 / cameraProjection.m11), near, far, 1 / far));
            float ratio = far / near;
            var depth = SystemInfo.usesReversedZBuffer
                ? new Vector4(ratio - 1, 1, (ratio - 1) / far, 1 / far)
                : new Vector4(1 - ratio, ratio, (1 - ratio) / far, ratio / far);
            commands.SetGlobalVector("_ZBufferParams", depth);
            var screen = new Vector4(size, size, 1 + 1f / size, 1 + 1f / size);
            commands.SetGlobalVector("_ScreenParams", screen);
            commands.SetGlobalVector("_ScaledScreenParams", screen);
            commands.SetGlobalVector("unity_OrthoParams", new Vector4(1 / Mathf.Abs(projection.m00), 1 / Mathf.Abs(projection.m11), 0, 1));
            commands.SetGlobalVector("_Time", Vector4.zero);
            commands.SetGlobalVector("_TimeParameters", new Vector4(0, 0, 1, 0));
            commands.SetGlobalVector("unity_DeltaTime", Vector4.zero);
            commands.SetGlobalVector("_SinTime", Vector4.zero);
            commands.SetGlobalVector("_CosTime", Vector4.one);
        }

        public void Dispose()
        {
            for (int i = 0; i < DisabledKeywords.Length; i++)
            {
                if (_keywords[i]) _commands.EnableShaderKeyword(DisabledKeywords[i]);
                else _commands.DisableShaderKeyword(DisabledKeywords[i]);
            }

            _commands.SetViewProjectionMatrices(_matrices[0], _matrices[1]);
            for (int i = 0; i < MatrixNames.Length; i++) _commands.SetGlobalMatrix(MatrixNames[i], _matrices[i]);
            for (int i = 0; i < VectorNames.Length; i++) _commands.SetGlobalVector(VectorNames[i], _vectors[i]);
        }
    }
}
