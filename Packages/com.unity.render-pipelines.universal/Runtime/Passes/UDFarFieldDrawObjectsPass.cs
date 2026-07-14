namespace UnityEngine.Rendering.Universal.Internal
{
    /// <summary>
    /// Far-field half of the near/far split (<see cref="UDNearFarSplit"/>). Draws far-field opaques
    /// with cheaper shader state, scoped to this pass and restored afterwards:
    /// - main light shadow keywords off — the far field keeps only the baked UDShadow,
    /// - _UDSoftShadows off — single-tap UDShadow sample instead of 4-tap PCF,
    /// - mono view direction — both eyes' camera positions collapse to the center eye, so
    ///   view-dependent shading (specular/fresnel/rim) stops diverging per eye. Geometry
    ///   projection stays stereo; only unity_StereoWorldSpaceCameraPos is overridden.
    /// </summary>
    internal class UDFarFieldDrawObjectsPass : DrawObjectsPass
    {
        static readonly int s_StereoWorldSpaceCameraPosId = Shader.PropertyToID("unity_StereoWorldSpaceCameraPos");
        static readonly GlobalKeyword s_UDSoftShadowsKeyword = GlobalKeyword.Create("_UDSoftShadows");

        static readonly Vector4[] s_MonoCameraPos = new Vector4[2];
        static readonly Vector4[] s_StereoCameraPos = new Vector4[2];

        public UDFarFieldDrawObjectsPass(string profilerTag, RenderPassEvent evt, RenderQueueRange renderQueueRange, LayerMask layerMask, StencilState stencilState, int stencilReference)
            : base(profilerTag, true, evt, renderQueueRange, layerMask, stencilState, stencilReference)
        {
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var cmd = renderingData.commandBuffer;

            cmd.DisableShaderKeyword(ShaderKeywordStrings.MainLightShadows);
            cmd.DisableShaderKeyword(ShaderKeywordStrings.MainLightShadowCascades);

            // QualitySetter drives this keyword through the immediate Shader API, so the
            // main-thread state is the correct restore target.
            bool udSoftShadows = Shader.IsKeywordEnabled(s_UDSoftShadowsKeyword);
            if (udSoftShadows)
                cmd.SetKeyword(s_UDSoftShadowsKeyword, false);

#if ENABLE_VR && ENABLE_XR_MODULE
            bool monoViewDir = renderingData.cameraData.xr.enabled && renderingData.cameraData.xr.singlePassEnabled;
            if (monoViewDir)
            {
                for (int viewIndex = 0; viewIndex < 2; ++viewIndex)
                    s_StereoCameraPos[viewIndex] = Matrix4x4.Inverse(renderingData.cameraData.xr.GetViewMatrix(viewIndex)).GetColumn(3);

                Vector4 centerEye = (s_StereoCameraPos[0] + s_StereoCameraPos[1]) * 0.5f;
                s_MonoCameraPos[0] = centerEye;
                s_MonoCameraPos[1] = centerEye;
                cmd.SetGlobalVectorArray(s_StereoWorldSpaceCameraPosId, s_MonoCameraPos);
            }
#endif

            base.Execute(context, ref renderingData);

            // Restore for the passes that follow this frame (skybox, transparents, features).
            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.MainLightShadows, MainLightShadowCasterPass.appliedMainLightShadowsKeyword);
            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.MainLightShadowCascades, MainLightShadowCasterPass.appliedMainLightShadowCascadesKeyword);
            if (udSoftShadows)
                cmd.SetKeyword(s_UDSoftShadowsKeyword, true);
#if ENABLE_VR && ENABLE_XR_MODULE
            if (monoViewDir)
                cmd.SetGlobalVectorArray(s_StereoWorldSpaceCameraPosId, s_StereoCameraPos);
#endif
        }
    }
}
