#if ENABLE_VR && ENABLE_XR_MODULE
using System;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Render all objects that have a 'XRMotionVectors' pass into the given depth buffer and motionvec buffer.
    /// When the eye texture depth is valid (XR depth submission), it is copied into the motion vector depth together
    /// with per-pixel camera motion, and only objects that moved are drawn on top.
    /// </summary>
    public class XRDepthMotionPass : ScriptableRenderPass
    {
        public const string k_MotionOnlyShaderTagIdName = "XRMotionVectors";
        private static readonly ShaderTagId k_MotionOnlyShaderTagId = new ShaderTagId(k_MotionOnlyShaderTagIdName);
        private static readonly int k_SpaceWarpNDCModifier = Shader.PropertyToID("_SpaceWarpNDCModifier");
        private static readonly int k_XRDepthTextureNameID = Shader.PropertyToID("_XRDepthTexture");
        private static readonly int k_XRDepthTextureScaleBiasNameID = Shader.PropertyToID("_XRDepthTexture_ST");
        private const int k_CameraMotionShaderPass = 0;
        private const int k_DepthCopyShaderPass = 1;
        private LocalKeyword m_SubsampleDepthKeyword;
        private RTHandle m_XRMotionVectorColor;
        private TextureHandle xrMotionVectorColor;
        private RTHandle m_XRMotionVectorDepth;
        private TextureHandle xrMotionVectorDepth;
        private bool m_XRSpaceWarpRightHandedNDC;

        // UNDERDOGS debug switches (DebugConfigMenu).
        // When false, valid eye depth is ignored: every object is drawn and the camera-motion fill runs,
        // like stock URP.
        public static bool useEyeDepthCopy = true;
        // When true, objects that did not move are drawn on top of the copied eye depth as well.
        // Without eye depth every object is always drawn.
        public static bool forceAllMotionVectorObjects = false;
        // Without eye depth only: when false, pixels without an object motion vector keep the cleared
        // zero velocity instead of the fullscreen camera-motion fill. The fill is drawn at the far plane.
        public static bool cameraMotionFill = true;

        // Last observed pass state, for diagnostics (see DescribeState).
        static bool s_lastPrevViewValid;
        static GraphicsFormat s_lastDepthStencilFormat;
        static GraphicsFormat s_lastColorFormat;
        static bool s_lastRightHandedNDC;
        static int s_lastWidth, s_lastHeight;
        static int s_lastEyeWidth, s_lastEyeHeight;
        static bool s_lastCopyDepth;
        static bool s_lastEyeDepthCopied;
        static bool s_lastSubsampleDepth;
        static bool s_stateLogged;

        public static int lastTargetWidth => s_lastWidth;
        public static int lastTargetHeight => s_lastHeight;
        public static bool lastEyeDepthCopied => s_lastEyeDepthCopied;

        public static string DescribeState()
        {
            return $"XRDepthMotionPass: target {s_lastWidth}x{s_lastHeight} color={s_lastColorFormat} depth={s_lastDepthStencilFormat} " +
                   $"eye {s_lastEyeWidth}x{s_lastEyeHeight} " +
                   $"xrCopyDepth={s_lastCopyDepth} eyeDepthCopied={s_lastEyeDepthCopied} subsampleDepth={s_lastSubsampleDepth} " +
                   $"rightHandedNDC={s_lastRightHandedNDC} prevViewValid={s_lastPrevViewValid} " +
                   $"useEyeDepthCopy={useEyeDepthCopy} forceAll={forceAllMotionVectorObjects} cameraFill={cameraMotionFill}";
        }

        /// <summary>
        /// Creates a new <c>XRDepthMotionPass</c> instance.
        /// </summary>
        /// <param name="evt">The <c>RenderPassEvent</c> to use.</param>
        /// <param name="xrMotionVector">The Shader used for rendering XR camera motion vector.</param>
        /// <seealso cref="RenderPassEvent"/>
        public XRDepthMotionPass(RenderPassEvent evt, Shader xrMotionVector)
        {
            base.profilingSampler = new ProfilingSampler(nameof(XRDepthMotionPass));
            renderPassEvent = evt;
            ResetMotionData();
            m_XRMotionVectorMaterial = CoreUtils.CreateEngineMaterial(xrMotionVector);
            xrMotionVectorColor = TextureHandle.nullHandle;
            m_XRMotionVectorColor = null;
            xrMotionVectorDepth = TextureHandle.nullHandle;
            m_XRMotionVectorDepth = null;
            m_SubsampleDepthKeyword = new LocalKeyword(xrMotionVector, "_SUBSAMPLE_DEPTH");
        }

        private const int k_XRViewCountPerPass = 2;
        private class PassData
        {
            internal RendererListHandle objMotionRendererList;
            internal Matrix4x4[] previousViewProjectionStereo = new Matrix4x4[k_XRViewCountPerPass];
            internal Matrix4x4[] viewProjectionStereo = new Matrix4x4[k_XRViewCountPerPass];
            internal Material xrMotionVector;
            internal bool hasValidXRDepth;
            internal TextureHandle xrDepthSrc;
            internal UniversalCameraData cameraData;
            internal bool requiresSubsampleDepth;
            internal LocalKeyword subsampleDepthKeyword;
            internal float ndcModifier;
            internal bool drawCameraMotionFill;
        }

        ///  View projection data
        private Matrix4x4[] m_StagingMatrixArray = new Matrix4x4[k_XRViewCountPerPass]; // Staging matrix to avoid allocating memory every frame when setting shader properties.
        private Matrix4x4[] m_PreviousStagingMatrixArray = new Matrix4x4[k_XRViewCountPerPass]; // Staging matrix to avoid allocating memory every frame when setting shader properties.
        private const int k_XRViewCount = 4;
        private Matrix4x4[] m_ViewProjection = new Matrix4x4[k_XRViewCount];
        private Matrix4x4[] m_PreviousViewProjection = new Matrix4x4[k_XRViewCount];
        private int m_LastFrameIndex;

        // Motion Vector
        private Material m_XRMotionVectorMaterial;

        private static DrawingSettings GetObjectMotionDrawingSettings(Camera camera)
        {
            var sortingSettings = new SortingSettings(camera) { criteria = SortingCriteria.CommonOpaque };
            // Notes: Usually, PerObjectData.MotionVectors will filter the renderer nodes to only draw moving objects.
            // In our case, we use forceAllMotionVectorObjects in the filteringSettings to draw idle objects as well to populate depth.
            var drawingSettings = new DrawingSettings(k_MotionOnlyShaderTagId, sortingSettings)
            {
                perObjectData = PerObjectData.MotionVectors,
                enableDynamicBatching = false,
                enableInstancing = true,
            };
            drawingSettings.SetShaderPassName(0, k_MotionOnlyShaderTagId);

            return drawingSettings;
        }

        private void InitObjectMotionRendererLists(ref PassData passData, ref CullingResults cullResults, RenderGraph renderGraph, Camera camera, bool drawAllObjects)
        {
            var objectMotionDrawingSettings = GetObjectMotionDrawingSettings(camera);

            // Draw motion vectors for all objects with matching shaders, including transparent
            var filteringSettings = new FilteringSettings(RenderQueueRange.all, camera.cullingMask);
            // Also render game objects that are not moved since last frame to save depth prepass requirement for camera motion.
            filteringSettings.forceAllMotionVectorObjects = drawAllObjects;
            var renderStateBlock = new RenderStateBlock(RenderStateMask.Nothing);

            RenderingUtils.CreateRendererListWithRenderStateBlock(renderGraph, ref cullResults, objectMotionDrawingSettings, filteringSettings, renderStateBlock, ref passData.objMotionRendererList);
        }

        /// <summary>
        /// Initialize the RenderGraph pass data.
        /// </summary>
        /// <param name="passData"></param>
        private void InitPassData(ref PassData passData, UniversalCameraData cameraData)
        {
            // XRTODO: Use XRSystem prevViewMatrix that is compatible with late latching. Currently blocked due to late latching engine side issue.
            //var gpuP0 = GL.GetGPUProjectionMatrix(cameraData.xr.GetProjMatrix(0), false);
            //var gpuP1 = GL.GetGPUProjectionMatrix(cameraData.xr.GetProjMatrix(1), false);
            //passData.viewProjectionStereo[0] = gpuP0 * cameraData.xr.GetViewMatrix(0);
            //passData.viewProjectionStereo[1] = gpuP1 * cameraData.xr.GetViewMatrix(1);
            //passData.previousViewProjectionStereo[0] = gpuP0 * cameraData.xr.GetPrevViewMatrix(0);
            //passData.previousViewProjectionStereo[1] = gpuP0 * cameraData.xr.GetPrevViewMatrix(1);

            // Setup matrices and shader
            var xr = cameraData.xr;
            var viewStartIndex = xr.viewCount * xr.multipassId;

            Array.Copy(m_PreviousViewProjection, viewStartIndex, m_PreviousStagingMatrixArray, 0, xr.viewCount);
            passData.previousViewProjectionStereo = m_PreviousStagingMatrixArray;

            Array.Copy(m_ViewProjection, viewStartIndex, m_StagingMatrixArray, 0, xr.viewCount);
            passData.viewProjectionStereo = m_StagingMatrixArray;

            // Setup camera motion material
            passData.xrMotionVector = m_XRMotionVectorMaterial;

            // Setup the default XR valid depth flag
            passData.hasValidXRDepth = false;
            passData.requiresSubsampleDepth = false;
            passData.subsampleDepthKeyword = m_SubsampleDepthKeyword;
            passData.cameraData = cameraData;
        }

        /// <summary>
        /// Import the XR motion color and depth targets into the RenderGraph.
        /// </summary>
        /// <param name="cameraData"> UniversalCameraData that holds XR pass data. </param>
        private void ImportXRMotionColorAndDepth(RenderGraph renderGraph, UniversalCameraData cameraData)
        {
            var rtMotionId = cameraData.xr.motionVectorRenderTarget;
            if (m_XRMotionVectorColor == null)
            {
                m_XRMotionVectorColor = RTHandles.Alloc(rtMotionId);
            }
            else if (m_XRMotionVectorColor.nameID != rtMotionId)
            {
                RTHandleStaticHelpers.SetRTHandleUserManagedWrapper(ref m_XRMotionVectorColor, rtMotionId);
            }

            // ID is the same since a RenderTexture encapsulates all the attachments, including both color+depth.
            var depthId = cameraData.xr.motionVectorRenderTarget;
            if (m_XRMotionVectorDepth == null)
            {
                m_XRMotionVectorDepth = RTHandles.Alloc(depthId);
            }
            else if (m_XRMotionVectorDepth.nameID != depthId)
            {
                RTHandleStaticHelpers.SetRTHandleUserManagedWrapper(ref m_XRMotionVectorDepth, depthId);
            }

            // Import motion color and depth into the render graph.
            RenderTargetInfo importInfo = new RenderTargetInfo();
            importInfo.width = cameraData.xr.motionVectorRenderTargetDesc.width;
            importInfo.height = cameraData.xr.motionVectorRenderTargetDesc.height;
            importInfo.volumeDepth = cameraData.xr.motionVectorRenderTargetDesc.volumeDepth;
            importInfo.msaaSamples = cameraData.xr.motionVectorRenderTargetDesc.msaaSamples;
            importInfo.format = cameraData.xr.motionVectorRenderTargetDesc.graphicsFormat;

            RenderTargetInfo importInfoDepth = new RenderTargetInfo();
            importInfoDepth = importInfo;
            importInfoDepth.format = cameraData.xr.motionVectorRenderTargetDesc.depthStencilFormat;

            ImportResourceParams importMotionColorParams = new ImportResourceParams();
            importMotionColorParams.clearOnFirstUse = true;
            importMotionColorParams.clearColor = Color.black;
            importMotionColorParams.discardOnLastUse = false;

            ImportResourceParams importMotionDepthParams = new ImportResourceParams();
            importMotionDepthParams.clearOnFirstUse = true;
            importMotionDepthParams.clearColor = Color.black;
            importMotionDepthParams.discardOnLastUse = false;

            xrMotionVectorColor = renderGraph.ImportTexture(m_XRMotionVectorColor, importInfo, importMotionColorParams);
            xrMotionVectorDepth = renderGraph.ImportTexture(m_XRMotionVectorDepth, importInfoDepth, importMotionDepthParams);

            m_XRSpaceWarpRightHandedNDC = cameraData.xr.spaceWarpRightHandedNDC;

            s_lastRightHandedNDC = m_XRSpaceWarpRightHandedNDC;
            s_lastDepthStencilFormat = importInfoDepth.format;
            s_lastColorFormat = importInfo.format;
            s_lastWidth = importInfo.width;
            s_lastHeight = importInfo.height;
            s_lastEyeWidth = cameraData.xr.renderTargetDesc.width;
            s_lastEyeHeight = cameraData.xr.renderTargetDesc.height;
            s_lastCopyDepth = cameraData.xr.copyDepth;
            s_lastPrevViewValid = cameraData.xr.GetPrevViewValid(0);
        }

        private static Vector4 GetScaleBias(RTHandle xrDepthSrc, UniversalCameraData cameraData)
        {
            bool yFlip = cameraData.IsHandleYFlipped(xrDepthSrc);

            Vector2 viewportScale = Vector2.one;

            if (cameraData.xr.IsXRTarget(xrDepthSrc))
            {
                // xrViewport is in pixel coordinates
                var xrViewport = cameraData.xr.GetViewport();
                viewportScale.x = xrViewport.width / cameraData.xr.renderTargetDesc.width;
                viewportScale.y = xrViewport.height / cameraData.xr.renderTargetDesc.height;
            }
            else if (xrDepthSrc.useScaling)
            {
                viewportScale.x = xrDepthSrc.rtHandleProperties.rtHandleScale.x;
                viewportScale.y = xrDepthSrc.rtHandleProperties.rtHandleScale.y;
            }

            return yFlip ? new Vector4(viewportScale.x, -viewportScale.y, 0, viewportScale.y) : new Vector4(viewportScale.x, viewportScale.y, 0, 0);
        }

#region Recording
        internal void Render(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

            // XR should be enabled and single pass should be enabled.
            if (!cameraData.xr.enabled || !cameraData.xr.singlePassEnabled)
            {
                Debug.LogWarning("XRDepthMotionPass::Render is skipped because either XR is not enabled or singlepass rendering is not enabled.");
                return;
            }

            // XR motion vector pass should be enabled.
            if (!cameraData.xr.hasMotionVectorPass)
            {
                Debug.LogWarning("XRDepthMotionPass::Render is skipped because XR motion vector is not enabled for the current XRPass.");
                return;
            }

            // First, import XR motion color and depth targets into the RenderGraph
            ImportXRMotionColorAndDepth(renderGraph, cameraData);

            // These flags are still required in SRP or the engine won't compute previous model matrices...
            // If the flag hasn't been set yet on this camera, motion vectors will skip a frame.
            cameraData.camera.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;

            // Start recording the pass
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("XR Motion Pass", out var passData, base.profilingSampler))
            {
                builder.EnableFoveatedRasterization(cameraData.xr.supportsFoveatedRendering);
                builder.SetExtendedFeatureFlags(ExtendedFeatureFlags.MultiviewRenderRegionsCompatible);
                // Setup Color and Depth attachments
                builder.SetRenderAttachment(xrMotionVectorColor, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(xrMotionVectorDepth, AccessFlags.Write);

                // Logic to detect if we already has valid XR depth data in the eye texture depth attachment
                bool hasValidXRDepth = cameraData.xr.copyDepth && useEyeDepthCopy;

                // In case we don't have valid depth, setup the renderer list to draw both static objects and moving objects to populate color+depth at the same time.
                bool drawAllObjects = !hasValidXRDepth || forceAllMotionVectorObjects;

                // Setup RendererList
                InitObjectMotionRendererLists(ref passData, ref renderingData.cullResults, renderGraph, cameraData.camera, drawAllObjects);
                builder.UseRendererList(passData.objMotionRendererList);

                // Allow setting up global matrix array
                builder.AllowGlobalStateModification(true);
                // Setup rest of the passData
                InitPassData(ref passData, cameraData);

                // Setup the relevant passData fields
                if (hasValidXRDepth)
                {
                    // backBufferDepth(eyeTexture depth) has valid data to read from
                    builder.UseTexture(resourceData.backBufferDepth, AccessFlags.Read);
                    passData.xrDepthSrc = resourceData.backBufferDepth;
                    passData.hasValidXRDepth = true;

                    // Subsample Depth if the motion vector render target is smaller than the color render target
                    passData.requiresSubsampleDepth = cameraData.xr.motionVectorRenderTargetDesc.width < cameraData.xr.renderTargetDesc.width;
                }

                s_lastEyeDepthCopied = passData.hasValidXRDepth;
                s_lastSubsampleDepth = passData.requiresSubsampleDepth;
                if (!s_stateLogged)
                {
                    s_stateLogged = true;
                    Debug.Log(DescribeState());
                }

                passData.ndcModifier = m_XRSpaceWarpRightHandedNDC ? -1.0f : 1.0f;
                passData.drawCameraMotionFill = cameraMotionFill;

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    if (data.hasValidXRDepth)
                    {
                        context.cmd.SetGlobalTexture(k_XRDepthTextureNameID, data.xrDepthSrc, RenderTextureSubElement.Depth);
                        context.cmd.SetGlobalVector(k_XRDepthTextureScaleBiasNameID, GetScaleBias(data.xrDepthSrc, data.cameraData));
                    }

                    ExecutePass(context.cmd, data);
                });
            }
        }
#endregion

        private static void ExecutePass(RasterCommandBuffer cmd, PassData data)
        {
            // Setup camera stereo buffer
            cmd.SetGlobalMatrixArray(ShaderPropertyId.previousViewProjectionNoJitterStereo, data.previousViewProjectionStereo);
            cmd.SetGlobalMatrixArray(ShaderPropertyId.viewProjectionNoJitterStereo, data.viewProjectionStereo);

            // SpaceWarp is only available on Vulkan, so these values are always true. This is to support 2 versions of spacewarp
            // One expects OpenGL NDC space motion vectors, the other expects Vulkan NDC space
            cmd.SetGlobalFloat(k_SpaceWarpNDCModifier, data.ndcModifier);

            // If we have valid depth data, copy the data to the motionvector depth to avoid rasterizing the static objects
            if (data.hasValidXRDepth)
            {
                if (data.requiresSubsampleDepth)
                    data.xrMotionVector.EnableKeyword(data.subsampleDepthKeyword);
                else
                    data.xrMotionVector.DisableKeyword(data.subsampleDepthKeyword);

                cmd.DrawProcedural(Matrix4x4.identity, data.xrMotionVector, k_DepthCopyShaderPass, MeshTopology.Triangles, 3, 1);
            }

            // Object Motion, fill stencil for mv filled pixels.
            cmd.DrawRendererList(data.objMotionRendererList);

            // Fill mv texture with camera motion for pixels that don't have mv stencil bit.
            if (!data.hasValidXRDepth && data.drawCameraMotionFill)
                cmd.DrawProcedural(Matrix4x4.identity, data.xrMotionVector, k_CameraMotionShaderPass, MeshTopology.Triangles, 3, 1);
        }

        private void ResetMotionData()
        {
            for (int i = 0; i < k_XRViewCount; i++)
            {
                m_ViewProjection[i] = Matrix4x4.identity;
                m_PreviousViewProjection[i] = Matrix4x4.identity;
            }
            m_LastFrameIndex = -1;
        }

        /// <summary>
        /// Update XRDepthMotionPass to use camera's view and projection matrix for motion vector calculation.
        /// </summary>
        /// <param name="cameraData"> The cameraData used for rendering to XR moition textures. </param>
        public void Update(ref UniversalCameraData cameraData)
        {
            if (!cameraData.xr.enabled || !cameraData.xr.singlePassEnabled)
            {
                Debug.LogWarning("XRDepthMotionPass::Update is skipped because either XR is not enabled or singlepass rendering is not enabled.");
                return;
            }

            if (m_LastFrameIndex != Time.frameCount)
            {
                {
                    var gpuVP0 = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrixNoJitter(0), renderIntoTexture: false) * cameraData.GetViewMatrix(0);
                    var gpuVP1 = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrixNoJitter(1), renderIntoTexture: false) * cameraData.GetViewMatrix(1);
                    var xr = cameraData.xr;
                    var viewStartIndex = xr.viewCount * xr.multipassId;
                    m_PreviousViewProjection[viewStartIndex] = m_ViewProjection[viewStartIndex];
                    m_PreviousViewProjection[viewStartIndex + 1] = m_ViewProjection[viewStartIndex + 1];
                    m_ViewProjection[viewStartIndex] = gpuVP0;
                    m_ViewProjection[viewStartIndex + 1] = gpuVP1;
                }
                if (cameraData.xr.isLastCameraPass)
                    m_LastFrameIndex = Time.frameCount;
            }
        }

        /// <summary>
        /// Cleans up resources used by the pass.
        /// </summary>
        public void Dispose()
        {
            m_XRMotionVectorColor?.Release();
            m_XRMotionVectorDepth?.Release();
            CoreUtils.Destroy(m_XRMotionVectorMaterial);
        }
    }
}
#endif
