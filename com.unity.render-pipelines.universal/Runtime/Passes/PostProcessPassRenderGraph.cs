using System.Runtime.CompilerServices;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal
{
    internal partial class PostProcessPass : ScriptableRenderPass
    {
        #region RenderGraph
        public class StopNaNsPassData
        {
            public TextureHandle targeTexture;
            public TextureHandle sourceTexture;
            public RenderingData renderingData;
            public Material stopNaN;
        }

        public void RenderStopNaN(in TextureHandle activeCameraColor, out TextureHandle destination, ref RenderingData renderingData)
        {
            var cameraData = renderingData.cameraData;
            var graph = renderingData.renderGraph;

            var cameraTargetDescriptor = renderingData.cameraData.cameraTargetDescriptor;
            var desc = PostProcessPass.GetCompatibleDescriptor(cameraTargetDescriptor,
                cameraTargetDescriptor.width,
                cameraTargetDescriptor.height,
                cameraTargetDescriptor.graphicsFormat,
                DepthBits.None);

            destination = UniversalRenderer.CreateRenderGraphTexture(graph, desc, "_StopNaNsTarget", true);

            bool useStopNan = cameraData.isStopNaNEnabled && m_Materials.stopNaN != null;
            // Optional NaN killer before post-processing kicks in
            // stopNaN may be null on Adreno 3xx. It doesn't support full shader level 3.5, but SystemInfo.graphicsShaderLevel is 35.
            if (useStopNan)
            {
                using (var builder = graph.AddRenderPass<StopNaNsPassData>("Stop NaNs", out var passData, ProfilingSampler.Get(URPProfileId.StopNaNs)))
                {


                    passData.targeTexture = builder.UseColorBuffer(destination, 0);
                    passData.sourceTexture = builder.ReadTexture(activeCameraColor);
                    passData.renderingData = renderingData;
                    passData.stopNaN = m_Materials.stopNaN;

                    //  TODO RENDERGRAPH: culling? force culluing off for testing
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((StopNaNsPassData data, RenderGraphContext context) =>
                    {
                        var cmd = data.renderingData.commandBuffer;
                        RenderingUtils.Blit(
                            cmd, data.sourceTexture, data.targeTexture, data.stopNaN, 0, data.renderingData.cameraData.xr.enabled,
                            RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store,
                            RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);
                    });

                    return;
                }
            }
        }

        public class SMAAPassData
        {
            public TextureHandle destinationTexture;
            public TextureHandle sourceTexture;
            public TextureHandle blendTexture;
            public RenderingData renderingData;
            public Material material;
        }

        public void RenderSMAA(in TextureHandle source, out TextureHandle destination, ref RenderingData renderingData)
        {
            var cameraData = renderingData.cameraData;
            var graph = renderingData.renderGraph;

            var cameraTargetDescriptor = renderingData.cameraData.cameraTargetDescriptor;
            cameraTargetDescriptor.useMipMap = false;
            cameraTargetDescriptor.autoGenerateMips = false;

            var desc = PostProcessPass.GetCompatibleDescriptor(cameraTargetDescriptor,
                cameraTargetDescriptor.width,
                cameraTargetDescriptor.height,
                cameraTargetDescriptor.graphicsFormat,
                DepthBits.None);
            destination = UniversalRenderer.CreateRenderGraphTexture(graph, desc, "_SMAATarget", true);

            // TODO RENDERGRAPH: look into depth target as stencil buffer case, in RenderGraph, it is not passible to use same RT as both color and depth. That is not supported.
            var edgeTextureDesc = PostProcessPass.GetCompatibleDescriptor(cameraTargetDescriptor,
                cameraTargetDescriptor.width,
                cameraTargetDescriptor.height,
                m_SMAAEdgeFormat,
                DepthBits.None);
            var edgeTexture = UniversalRenderer.CreateRenderGraphTexture(graph, edgeTextureDesc, "_EdgeTexture", true);

            var blendTextureDesc = PostProcessPass.GetCompatibleDescriptor(cameraTargetDescriptor,
                cameraTargetDescriptor.width,
                cameraTargetDescriptor.height,
                GraphicsFormat.R8G8B8A8_UNorm,
                DepthBits.None);
            var blendTexture = UniversalRenderer.CreateRenderGraphTexture(graph, blendTextureDesc, "_BlendTexture", true);

            bool useSubPixelMorpAA = cameraData.antialiasing == AntialiasingMode.SubpixelMorphologicalAntiAliasing && SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES2;

            // Anti-aliasing
            if (useSubPixelMorpAA)
            {
                var material = m_Materials.subpixelMorphologicalAntialiasing;
                const int kStencilBit = 64;

                // Globals
                material.SetVector(ShaderConstants._Metrics, new Vector4(1f / cameraTargetDescriptor.width, 1f / cameraTargetDescriptor.height, cameraTargetDescriptor.width, cameraTargetDescriptor.height));
                material.SetTexture(ShaderConstants._AreaTexture, m_Data.textures.smaaAreaTex);
                material.SetTexture(ShaderConstants._SearchTexture, m_Data.textures.smaaSearchTex);
                material.SetFloat(ShaderConstants._StencilRef, (float)kStencilBit);
                material.SetFloat(ShaderConstants._StencilMask, (float)kStencilBit);

                // Quality presets
                material.shaderKeywords = null;

                switch (cameraData.antialiasingQuality)
                {
                    case AntialiasingQuality.Low:
                        material.EnableKeyword(ShaderKeywordStrings.SmaaLow);
                        break;
                    case AntialiasingQuality.Medium:
                        material.EnableKeyword(ShaderKeywordStrings.SmaaMedium);
                        break;
                    case AntialiasingQuality.High:
                        material.EnableKeyword(ShaderKeywordStrings.SmaaHigh);
                        break;
                }

                using (var builder = graph.AddRenderPass<SMAAPassData>("SMAA Edge Detection", out var passData, ProfilingSampler.Get(URPProfileId.SMAA)))
                {
                    //passData.destinationTexture = builder.UseDepthBuffer(edgeTexture, DepthAccess.Write);
                    passData.destinationTexture = builder.UseColorBuffer(edgeTexture, 0);

                    passData.sourceTexture = builder.ReadTexture(source);
                    passData.renderingData = renderingData;
                    passData.material = material;

                    //  TODO RENDERGRAPH: culling? force culluing off for testing
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((SMAAPassData data, RenderGraphContext context) =>
                    {
                        var pixelRect = data.renderingData.cameraData.pixelRect;
                        var material = data.material;

                        var cmd = data.renderingData.commandBuffer;

                        // Prepare for manual blit
                        cmd.SetViewport(pixelRect);

                        // Pass 1: Edge detection
                        cmd.ClearRenderTarget(RTClearFlags.ColorStencil, Color.clear, 1.0f, 0);
                        cmd.SetGlobalTexture(ShaderConstants._ColorTexture, passData.sourceTexture);
                        DrawFullscreenMesh(cmd, material, 0, data.renderingData.cameraData.xr.enabled);
                    });
                }

                using (var builder = graph.AddRenderPass<SMAAPassData>("SMAA Blend weights", out var passData, ProfilingSampler.Get(URPProfileId.SMAA)))
                {
                    passData.destinationTexture = builder.UseColorBuffer(blendTexture, 0);
                    //passData.destinationTexture = builder.UseDepthBuffer(edgeTexture, DepthAccess.Read);
                    passData.sourceTexture = builder.ReadTexture(edgeTexture);
                    passData.renderingData = renderingData;
                    passData.material = material;

                    //  TODO RENDERGRAPH: culling? force culluing off for testing
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((SMAAPassData data, RenderGraphContext context) =>
                    {
                        var pixelRect = data.renderingData.cameraData.pixelRect;
                        var material = data.material;

                        var cmd = data.renderingData.commandBuffer;

                        // Prepare for manual blit
                        cmd.SetViewport(pixelRect);

                        // Pass 2: Blend weights
                        cmd.ClearRenderTarget(false, true, Color.clear);
                        cmd.SetGlobalTexture(ShaderConstants._ColorTexture, passData.sourceTexture);
                        DrawFullscreenMesh(cmd, material, 1, data.renderingData.cameraData.xr.enabled);
                    });
                }

                using (var builder = graph.AddRenderPass<SMAAPassData>("SMAA Neighborhood blending", out var passData, ProfilingSampler.Get(URPProfileId.SMAA)))
                {
                    passData.destinationTexture = builder.UseColorBuffer(destination, 0);
                    passData.sourceTexture = builder.ReadTexture(source);
                    passData.blendTexture = builder.ReadTexture(blendTexture);
                    passData.renderingData = renderingData;
                    passData.material = material;

                    //  TODO RENDERGRAPH: culling? force culluing off for testing
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((SMAAPassData data, RenderGraphContext context) =>
                    {
                        var pixelRect = data.renderingData.cameraData.pixelRect;
                        var material = data.material;
                        var cmd = data.renderingData.commandBuffer;

                        // Prepare for manual blit
                        cmd.SetViewport(pixelRect);

                        // Pass 3: Neighborhood blending
                        cmd.ClearRenderTarget(false, true, Color.clear);
                        cmd.SetGlobalTexture(ShaderConstants._ColorTexture, passData.sourceTexture);
                        cmd.SetGlobalTexture(ShaderConstants._BlendTexture, passData.blendTexture);
                        DrawFullscreenMesh(cmd, material, 2, data.renderingData.cameraData.xr.enabled);
                    });
                }
            }
        }

        public class DoFGaussianPassData
        {
            public TextureHandle cocTexture;
            public TextureHandle colorTexture;
            public TextureHandle sourceTexture;
            public RenderingData renderingData;
            public Material material;
        }

        public class DoFBokehPassData
        {
            public TextureHandle cocTexture;
            public TextureHandle dofTexture;
            public TextureHandle sourceTexture;
            public RenderingData renderingData;
            public Material material;
        }

        public void RenderDoF(in TextureHandle source, out TextureHandle destination, ref RenderingData renderingData)
        {
            // TODO RENDERGRAPH: use helper function for setting up member vars
            var stack = VolumeManager.instance.stack;
            m_DepthOfField = stack.GetComponent<DepthOfField>();

            var cameraData = renderingData.cameraData;
            var graph = renderingData.renderGraph;

            var dofMaterial = m_DepthOfField.mode.value == DepthOfFieldMode.Gaussian ? m_Materials.gaussianDepthOfField : m_Materials.bokehDepthOfField;
            bool useDepthOfField = m_DepthOfField.IsActive() && !renderingData.cameraData.isSceneViewCamera && dofMaterial != null;

            // TODO RENDERGRAPH: use member variable instead
            var cameraTargetDescriptor = renderingData.cameraData.cameraTargetDescriptor;
            cameraTargetDescriptor.useMipMap = false;
            cameraTargetDescriptor.autoGenerateMips = false;

            var desc = PostProcessPass.GetCompatibleDescriptor(cameraTargetDescriptor,
                cameraTargetDescriptor.width,
                cameraTargetDescriptor.height,
                cameraTargetDescriptor.graphicsFormat,
                DepthBits.None);
            destination = UniversalRenderer.CreateRenderGraphTexture(graph, desc, "_DoFTarget", true);

            // Depth of Field
            // Adreno 3xx SystemInfo.graphicsShaderLevel is 35, but instancing support is disabled due to buggy drivers.
            // DOF shader uses #pragma target 3.5 which adds requirement for instancing support, thus marking the shader unsupported on those devices.
            if (useDepthOfField)
            {
                var markerName = m_DepthOfField.mode.value == DepthOfFieldMode.Gaussian
                    ? URPProfileId.GaussianDepthOfField
                    : URPProfileId.BokehDepthOfField;

                if (m_DepthOfField.mode.value == DepthOfFieldMode.Gaussian)
                {
                    int downSample = 2;
                    var material = dofMaterial;
                    int wh = cameraTargetDescriptor.width / downSample;
                    int hh = cameraTargetDescriptor.height / downSample;
                    float farStart = m_DepthOfField.gaussianStart.value;
                    float farEnd = Mathf.Max(farStart, m_DepthOfField.gaussianEnd.value);
                    var cmd = renderingData.commandBuffer;

                    // Assumes a radius of 1 is 1 at 1080p
                    // Past a certain radius our gaussian kernel will look very bad so we'll clamp it for
                    // very high resolutions (4K+).
                    float maxRadius = m_DepthOfField.gaussianMaxRadius.value * (wh / 1080f);
                    maxRadius = Mathf.Min(maxRadius, 2f);

                    CoreUtils.SetKeyword(material, ShaderKeywordStrings.HighQualitySampling, m_DepthOfField.highQualitySampling.value);
                    material.SetVector(ShaderConstants._CoCParams, new Vector3(farStart, farEnd, maxRadius));

                    // Temporary textures
                    // TODO RENDERGRAPH: FilterMode.Bilinear
                    var fullCoCTextureDesc = PostProcessPass.GetCompatibleDescriptor(cameraTargetDescriptor, cameraTargetDescriptor.width, cameraTargetDescriptor.height, m_GaussianCoCFormat);
                    var fullCoCTexture = UniversalRenderer.CreateRenderGraphTexture(graph, fullCoCTextureDesc, "_FullCoCTexture", true);
                    var halfCoCTextureDesc = PostProcessPass.GetCompatibleDescriptor(cameraTargetDescriptor, wh, hh, m_GaussianCoCFormat);
                    var halfCoCTexture = UniversalRenderer.CreateRenderGraphTexture(graph, halfCoCTextureDesc, "_HalfCoCTexture", true);
                    var pingTextureDesc = PostProcessPass.GetCompatibleDescriptor(cameraTargetDescriptor, wh, hh, m_DefaultHDRFormat);
                    var pingTexture = UniversalRenderer.CreateRenderGraphTexture(graph, pingTextureDesc, "_PingTexture", true);
                    var pongTextureDesc = PostProcessPass.GetCompatibleDescriptor(cameraTargetDescriptor, wh, hh, m_DefaultHDRFormat);
                    var pongTexture = UniversalRenderer.CreateRenderGraphTexture(graph, pongTextureDesc, "_PongTexture", true);

                    // TODO RENDERGRAPH: this line is for postFX dynamic resolution without RTHandle, we should consider remove this line in favor of RTHandles
                    PostProcessUtils.SetSourceSize(cmd, cameraTargetDescriptor);
                    // TODO RENDERGRAPH: should not call cmd here, move it into render graph renderfunc
                    cmd.SetGlobalVector(ShaderConstants._DownSampleScaleFactor, new Vector4(1.0f / downSample, 1.0f / downSample, downSample, downSample));

                    using (var builder = graph.AddRenderPass<DoFGaussianPassData>("Depth of Field - Compute CoC", out var passData, ProfilingSampler.Get(markerName)))
                    {
                        builder.UseColorBuffer(fullCoCTexture, 0);
                        passData.sourceTexture = builder.ReadTexture(source);
                        passData.renderingData = renderingData;
                        passData.material = material;

                        //  TODO RENDERGRAPH: culling? force culluing off for testing
                        builder.AllowPassCulling(false);

                        builder.SetRenderFunc((DoFGaussianPassData data, RenderGraphContext context) =>
                        {
                            var material = data.material;
                            var cmd = data.renderingData.commandBuffer;

                            // Compute CoC
                            cmd.SetGlobalTexture(ShaderPropertyId.sourceTex, data.sourceTexture);
                            DrawFullscreenMesh(cmd, material, 0, data.renderingData.cameraData.xr.enabled);
                        });
                    }

                    using (var builder = graph.AddRenderPass<DoFGaussianPassData>("Depth of Field - Downscale & Prefilter Color + CoC", out var passData, ProfilingSampler.Get(markerName)))
                    {
                        builder.UseColorBuffer(halfCoCTexture, 0);
                        builder.UseColorBuffer(pingTexture, 1);
                        // TODO RENDERGRAPH: investigate - Setting MRTs without a depth buffer is not supported.
                        builder.UseDepthBuffer(halfCoCTexture, DepthAccess.ReadWrite);

                        passData.sourceTexture = builder.ReadTexture(source);
                        passData.cocTexture = builder.ReadTexture(fullCoCTexture);

                        passData.renderingData = renderingData;
                        passData.material = material;

                        //  TODO RENDERGRAPH: culling? force culluing off for testing
                        builder.AllowPassCulling(false);

                        builder.SetRenderFunc((DoFGaussianPassData data, RenderGraphContext context) =>
                        {
                            var material = data.material;
                            var cmd = data.renderingData.commandBuffer;
                            var pixelRect = data.renderingData.cameraData.pixelRect;

                            // Downscale & prefilter color + coc
                            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                            //  TODO RENDERGRAPH: figure out why setViewport will break rendering, looks like cmd.SetRenderTarget in non RG pass calls setViewport implicitly
                            //cmd.SetViewport(pixelRect);
                            cmd.SetGlobalTexture(ShaderConstants._ColorTexture, data.sourceTexture);
                            cmd.SetGlobalTexture(ShaderConstants._FullCoCTexture, data.cocTexture);
                            DrawFullscreenMesh(cmd, material, 1, data.renderingData.cameraData.xr.enabled);
                            cmd.SetViewProjectionMatrices(data.renderingData.cameraData.camera.worldToCameraMatrix, data.renderingData.cameraData.camera.projectionMatrix);
                        });
                    }

                    using (var builder = graph.AddRenderPass<DoFGaussianPassData>("Depth of Field - Blur H", out var passData, ProfilingSampler.Get(markerName)))
                    {
                        builder.UseColorBuffer(pongTexture, 0);
                        passData.sourceTexture = builder.ReadTexture(pingTexture);
                        passData.cocTexture = builder.ReadTexture(halfCoCTexture);

                        passData.renderingData = renderingData;
                        passData.material = material;

                        //  TODO RENDERGRAPH: culling? force culluing off for testing
                        builder.AllowPassCulling(false);

                        builder.SetRenderFunc((DoFGaussianPassData data, RenderGraphContext context) =>
                        {
                            var material = data.material;
                            var cmd = data.renderingData.commandBuffer;

                            // Blur
                            cmd.SetGlobalTexture(ShaderConstants._HalfCoCTexture, data.cocTexture);
                            cmd.SetGlobalTexture(ShaderPropertyId.sourceTex, data.sourceTexture);
                            DrawFullscreenMesh(cmd, material, 2, data.renderingData.cameraData.xr.enabled);
                        });
                    }

                    using (var builder = graph.AddRenderPass<DoFGaussianPassData>("Depth of Field - Blur V", out var passData, ProfilingSampler.Get(markerName)))
                    {
                        builder.UseColorBuffer(pingTexture, 0);
                        passData.sourceTexture = builder.ReadTexture(pongTexture);
                        passData.cocTexture = builder.ReadTexture(halfCoCTexture);

                        passData.renderingData = renderingData;
                        passData.material = material;

                        //  TODO RENDERGRAPH: culling? force culluing off for testing
                        builder.AllowPassCulling(false);

                        builder.SetRenderFunc((DoFGaussianPassData data, RenderGraphContext context) =>
                        {
                            var material = data.material;
                            var cmd = data.renderingData.commandBuffer;

                            // Blur
                            cmd.SetGlobalTexture(ShaderConstants._HalfCoCTexture, data.cocTexture);
                            cmd.SetGlobalTexture(ShaderPropertyId.sourceTex, data.sourceTexture);
                            DrawFullscreenMesh(cmd, material, 3, data.renderingData.cameraData.xr.enabled);
                        });
                    }

                    using (var builder = graph.AddRenderPass<DoFGaussianPassData>("Depth of Field - Composite", out var passData, ProfilingSampler.Get(markerName)))
                    {
                        builder.UseColorBuffer(destination, 0);
                        passData.sourceTexture = builder.ReadTexture(source);
                        passData.cocTexture = builder.ReadTexture(fullCoCTexture);
                        passData.colorTexture = builder.ReadTexture(pingTexture);

                        passData.renderingData = renderingData;
                        passData.material = material;

                        //  TODO RENDERGRAPH: culling? force culluing off for testing
                        builder.AllowPassCulling(false);

                        builder.SetRenderFunc((DoFGaussianPassData data, RenderGraphContext context) =>
                        {
                            var material = data.material;
                            var cmd = data.renderingData.commandBuffer;

                            // Composite
                            cmd.SetGlobalTexture(ShaderConstants._ColorTexture, data.colorTexture);
                            cmd.SetGlobalTexture(ShaderConstants._FullCoCTexture, data.cocTexture);
                            cmd.SetGlobalTexture(ShaderPropertyId.sourceTex, data.sourceTexture);
                            DrawFullscreenMesh(cmd, material, 4, data.renderingData.cameraData.xr.enabled);
                        });
                    }
                }
                else if (m_DepthOfField.mode.value == DepthOfFieldMode.Bokeh)
                {
                    // DoBokehDepthOfField(cmd, source, destination, pixelRect);
                    int downSample = 2;
                    var material = dofMaterial;
                    int wh = cameraTargetDescriptor.width / downSample;
                    int hh = cameraTargetDescriptor.height / downSample;

                    // "A Lens and Aperture Camera Model for Synthetic Image Generation" [Potmesil81]
                    float F = m_DepthOfField.focalLength.value / 1000f;
                    float A = m_DepthOfField.focalLength.value / m_DepthOfField.aperture.value;
                    float P = m_DepthOfField.focusDistance.value;
                    float maxCoC = (A * F) / (P - F);
                    float maxRadius = GetMaxBokehRadiusInPixels(cameraTargetDescriptor.height);
                    float rcpAspect = 1f / (wh / (float)hh);
                    var cmd = renderingData.commandBuffer;

                    CoreUtils.SetKeyword(material, ShaderKeywordStrings.UseFastSRGBLinearConversion, m_UseFastSRGBLinearConversion);
                    // TODO RENDERGRAPH: should not call cmd here, move it into render graph renderfunc
                    cmd.SetGlobalVector(ShaderConstants._CoCParams, new Vector4(P, maxCoC, maxRadius, rcpAspect));

                    // Prepare the bokeh kernel constant buffer
                    int hash = m_DepthOfField.GetHashCode();
                    if (hash != m_BokehHash || maxRadius != m_BokehMaxRadius || rcpAspect != m_BokehRCPAspect)
                    {
                        m_BokehHash = hash;
                        m_BokehMaxRadius = maxRadius;
                        m_BokehRCPAspect = rcpAspect;
                        PrepareBokehKernel(maxRadius, rcpAspect);
                    }

                    // TODO RENDERGRAPH: should not call cmd here, move it into render graph renderfunc
                    cmd.SetGlobalVectorArray(ShaderConstants._BokehKernel, m_BokehKernel);

                    // Temporary textures
                    // TODO RENDERGRAPH: FilterMode.Bilinear
                    var fullCoCTextureDesc = PostProcessPass.GetCompatibleDescriptor(cameraTargetDescriptor, cameraTargetDescriptor.width, cameraTargetDescriptor.height, GraphicsFormat.R8_UNorm);
                    var fullCoCTexture = UniversalRenderer.CreateRenderGraphTexture(graph, fullCoCTextureDesc, "_FullCoCTexture", true);
                    var pingTextureDesc = PostProcessPass.GetCompatibleDescriptor(cameraTargetDescriptor, wh, hh, GraphicsFormat.R16G16B16A16_SFloat);
                    var pingTexture = UniversalRenderer.CreateRenderGraphTexture(graph, pingTextureDesc, "_PingTexture", true);
                    var pongTextureDesc = PostProcessPass.GetCompatibleDescriptor(cameraTargetDescriptor, wh, hh, GraphicsFormat.R16G16B16A16_SFloat);
                    var pongTexture = UniversalRenderer.CreateRenderGraphTexture(graph, pongTextureDesc, "_PongTexture", true);

                    // TODO RENDERGRAPH: should not call cmd here, move it into render graph renderfunc
                    PostProcessUtils.SetSourceSize(cmd, m_Descriptor);
                    cmd.SetGlobalVector(ShaderConstants._DownSampleScaleFactor, new Vector4(1.0f / downSample, 1.0f / downSample, downSample, downSample));
                    float uvMargin = (1.0f / m_Descriptor.height) * downSample;
                    cmd.SetGlobalVector(ShaderConstants._BokehConstants, new Vector4(uvMargin, uvMargin * 2.0f));

                    using (var builder = graph.AddRenderPass<DoFBokehPassData>("Depth of Field - Compute CoC", out var passData, ProfilingSampler.Get(markerName)))
                    {
                        builder.UseColorBuffer(fullCoCTexture, 0);
                        passData.sourceTexture = builder.ReadTexture(source);
                        passData.renderingData = renderingData;
                        passData.material = material;

                        //  TODO RENDERGRAPH: culling? force culluing off for testing
                        builder.AllowPassCulling(false);

                        builder.SetRenderFunc((DoFBokehPassData data, RenderGraphContext context) =>
                        {
                            var material = data.material;
                            var cmd = data.renderingData.commandBuffer;

                            // Compute CoC
                            cmd.SetGlobalTexture(ShaderPropertyId.sourceTex, data.sourceTexture);
                            DrawFullscreenMesh(cmd, material, 0, data.renderingData.cameraData.xr.enabled);
                        });
                    }

                    using (var builder = graph.AddRenderPass<DoFBokehPassData>("Depth of Field - Downscale & Prefilter Color + CoC", out var passData, ProfilingSampler.Get(markerName)))
                    {
                        builder.UseColorBuffer(pingTexture, 0);
                        passData.sourceTexture = builder.ReadTexture(source);
                        passData.cocTexture = builder.ReadTexture(fullCoCTexture);
                        passData.renderingData = renderingData;
                        passData.material = material;

                        //  TODO RENDERGRAPH: culling? force culluing off for testing
                        builder.AllowPassCulling(false);

                        builder.SetRenderFunc((DoFBokehPassData data, RenderGraphContext context) =>
                        {
                            var material = data.material;
                            var cmd = data.renderingData.commandBuffer;

                            // Downscale & prefilter color + coc
                            cmd.SetGlobalTexture(ShaderConstants._FullCoCTexture, data.cocTexture);
                            DrawFullscreenMesh(cmd, material, 1, data.renderingData.cameraData.xr.enabled);
                        });
                    }

                    using (var builder = graph.AddRenderPass<DoFBokehPassData>("Depth of Field - Bokeh Blur", out var passData, ProfilingSampler.Get(markerName)))
                    {
                        builder.UseColorBuffer(pongTexture, 0);
                        passData.sourceTexture = builder.ReadTexture(pingTexture);
                        passData.renderingData = renderingData;
                        passData.material = material;

                        //  TODO RENDERGRAPH: culling? force culluing off for testing
                        builder.AllowPassCulling(false);

                        builder.SetRenderFunc((DoFBokehPassData data, RenderGraphContext context) =>
                        {
                            var material = data.material;
                            var cmd = data.renderingData.commandBuffer;

                            // Downscale & prefilter color + coc
                            cmd.SetGlobalTexture(ShaderPropertyId.sourceTex, data.sourceTexture);
                            DrawFullscreenMesh(cmd, material, 2, data.renderingData.cameraData.xr.enabled);
                        });
                    }

                    using (var builder = graph.AddRenderPass<DoFBokehPassData>("Depth of Field - Post-filtering", out var passData, ProfilingSampler.Get(markerName)))
                    {
                        builder.UseColorBuffer(pingTexture, 0);
                        passData.sourceTexture = builder.ReadTexture(pongTexture);
                        passData.renderingData = renderingData;
                        passData.material = material;

                        //  TODO RENDERGRAPH: culling? force culluing off for testing
                        builder.AllowPassCulling(false);

                        builder.SetRenderFunc((DoFBokehPassData data, RenderGraphContext context) =>
                        {
                            var material = data.material;
                            var cmd = data.renderingData.commandBuffer;

                            // Post - filtering
                            // TODO RENDERGRAPH: Look into loadstore op in BlitDstDiscardContent
                            cmd.SetGlobalTexture(ShaderPropertyId.sourceTex, data.sourceTexture);
                            DrawFullscreenMesh(cmd, material, 3, data.renderingData.cameraData.xr.enabled);
                        });
                    }

                    using (var builder = graph.AddRenderPass<DoFBokehPassData>("Depth of Field - Composite", out var passData, ProfilingSampler.Get(markerName)))
                    {
                        builder.UseColorBuffer(destination, 0);
                        passData.sourceTexture = builder.ReadTexture(source);
                        passData.dofTexture = builder.ReadTexture(pingTexture);

                        passData.renderingData = renderingData;
                        passData.material = material;

                        //  TODO RENDERGRAPH: culling? force culluing off for testing
                        builder.AllowPassCulling(false);

                        builder.SetRenderFunc((DoFBokehPassData data, RenderGraphContext context) =>
                        {
                            var material = data.material;
                            var cmd = data.renderingData.commandBuffer;

                            // Composite
                            // TODO RENDERGRAPH: Look into loadstore op in BlitDstDiscardContent
                            cmd.SetGlobalTexture(ShaderConstants._DofTexture, data.dofTexture);
                            cmd.SetGlobalTexture(ShaderPropertyId.sourceTex, data.sourceTexture);
                            DrawFullscreenMesh(cmd, material, 4, data.renderingData.cameraData.xr.enabled);
                        });
                    }
                }
            }
        }

        public class PaniniProjectionPassData
        {
            public TextureHandle destinationTexture;
            public TextureHandle sourceTexture;
            public RenderingData renderingData;
            public Material material;
        }

        public void RenderPaniniProjection(in TextureHandle source, out TextureHandle destination, ref RenderingData renderingData)
        {
            // TODO RENDERGRAPH: use helper function for setting up member vars
            var stack = VolumeManager.instance.stack;
            m_PaniniProjection = stack.GetComponent<PaniniProjection>();

            var camera = renderingData.cameraData.camera;
            var cmd = renderingData.commandBuffer;
            var cameraData = renderingData.cameraData;
            var graph = renderingData.renderGraph;
            bool isSceneViewCamera = cameraData.isSceneViewCamera;

            var cameraTargetDescriptor = cameraData.cameraTargetDescriptor;
            var desc = PostProcessPass.GetCompatibleDescriptor(cameraTargetDescriptor,
                cameraTargetDescriptor.width,
                cameraTargetDescriptor.height,
                cameraTargetDescriptor.graphicsFormat,
                DepthBits.None);

            destination = UniversalRenderer.CreateRenderGraphTexture(graph, desc, "_PaniniProjectionTarget", true);

            bool usePaniniProjection = m_PaniniProjection.IsActive() && !isSceneViewCamera;
            // Optional NaN killer before post-processing kicks in
            // stopNaN may be null on Adreno 3xx. It doesn't support full shader level 3.5, but SystemInfo.graphicsShaderLevel is 35.
            if (usePaniniProjection)
            {
                float distance = m_PaniniProjection.distance.value;
                var viewExtents = CalcViewExtents(camera);
                var cropExtents = CalcCropExtents(camera, distance);

                float scaleX = cropExtents.x / viewExtents.x;
                float scaleY = cropExtents.y / viewExtents.y;
                float scaleF = Mathf.Min(scaleX, scaleY);

                float paniniD = distance;
                float paniniS = Mathf.Lerp(1f, Mathf.Clamp01(scaleF), m_PaniniProjection.cropToFit.value);

                var material = m_Materials.paniniProjection;

                material.SetVector(ShaderConstants._Params, new Vector4(viewExtents.x, viewExtents.y, paniniD, paniniS));
                material.EnableKeyword(
                    1f - Mathf.Abs(paniniD) > float.Epsilon
                    ? ShaderKeywordStrings.PaniniGeneric : ShaderKeywordStrings.PaniniUnitDistance
                );

                using (var builder = graph.AddRenderPass<PaniniProjectionPassData>("Panini Projection", out var passData, ProfilingSampler.Get(URPProfileId.PaniniProjection)))
                {
                    passData.destinationTexture = builder.UseColorBuffer(destination, 0);
                    passData.sourceTexture = builder.ReadTexture(source);
                    passData.renderingData = renderingData;
                    passData.material = material;

                    // TODO RENDERGRAPH: culling? force culluing off for testing
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((PaniniProjectionPassData data, RenderGraphContext context) =>
                    {
                        var cmd = data.renderingData.commandBuffer;
                        // TODO RENDERGRAPH: BlitDstDiscardContent
                        cmd.SetGlobalTexture(ShaderPropertyId.sourceTex, data.sourceTexture);
                        DrawFullscreenMesh(cmd, material, 0, data.renderingData.cameraData.xr.enabled);
                    });

                    return;
                }
            }
        }

        public class LensFlarePassData
        {
            public TextureHandle destinationTexture;
            public RenderingData renderingData;
            public Material material;
            public bool usePanini;
            public float paniniDistance;
            public float paniniCropToFit;
        }

        public void RenderLensFlareDatadriven(in TextureHandle destination, ref RenderingData renderingData)
        {
            // TODO RENDERGRAPH: use helper function for setting up member vars
            var stack = VolumeManager.instance.stack;
            m_PaniniProjection = stack.GetComponent<PaniniProjection>();
            bool useLensFlare = !LensFlareCommonSRP.Instance.IsEmpty();

            if (useLensFlare)
            {
                bool usePanini;
                float paniniDistance;
                float paniniCropToFit;
                if (m_PaniniProjection.IsActive())
                {
                    usePanini = true;
                    paniniDistance = m_PaniniProjection.distance.value;
                    paniniCropToFit = m_PaniniProjection.cropToFit.value;
                }
                else
                {
                    usePanini = false;
                    paniniDistance = 1.0f;
                    paniniCropToFit = 1.0f;
                }

                var graph = renderingData.renderGraph;
                using (var builder = graph.AddRenderPass<LensFlarePassData>("Lens Flare Pass", out var passData, ProfilingSampler.Get(URPProfileId.LensFlareDataDriven)))
                {
                    // TODO RENDERGRAPH: call WriteTexture here because DoLensFlareDataDrivenCommon will call SetRenderTarget internally.
                    passData.destinationTexture = builder.WriteTexture(destination);
                    passData.renderingData = renderingData;
                    passData.material = m_Materials.lensFlareDataDriven;
                    passData.usePanini = usePanini;
                    passData.paniniDistance = paniniDistance;
                    passData.paniniCropToFit = paniniCropToFit;

                    // TODO RENDERGRAPH: culling? force culluing off for testing
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((LensFlarePassData data, RenderGraphContext context) =>
                    {
                        var cmd = data.renderingData.commandBuffer;
                        var camera = data.renderingData.cameraData.camera;
                        var cameraTargetDescriptor = data.renderingData.cameraData.cameraTargetDescriptor;
                        var usePanini = data.usePanini;
                        var paniniDistance = data.paniniDistance;
                        var paniniCropToFit = data.paniniCropToFit;
                        var destination = data.destinationTexture;
                        var material = data.material;

                        var gpuView = camera.worldToCameraMatrix;
                        var gpuNonJitteredProj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
                        // Zero out the translation component.
                        gpuView.SetColumn(3, new Vector4(0, 0, 0, 1));
                        var gpuVP = gpuNonJitteredProj * camera.worldToCameraMatrix;

                        // TODO RENDERGRAPH: DoLensFlareDataDrivenCommon will call set render target internally. Remove the set render target call?
                        // TODO RENDERGRAPH: LensFlareCommonSRP internally manages RTHandle occlusionRT, we should properly register this RTHandle to RenderGraph
                        LensFlareCommonSRP.DoLensFlareDataDrivenCommon(material, LensFlareCommonSRP.Instance, camera, (float)cameraTargetDescriptor.width, (float)cameraTargetDescriptor.height,
                            usePanini, paniniDistance, paniniCropToFit,
                            true,
                            camera.transform.position,
                            gpuVP,
                            cmd, destination,
                            (Light light, Camera cam, Vector3 wo) => { return GetLensFlareLightAttenuation(light, cam, wo); },
                            ShaderConstants._FlareOcclusionTex, ShaderConstants._FlareOcclusionIndex,
                            ShaderConstants._FlareTex, ShaderConstants._FlareColorValue,
                            ShaderConstants._FlareData0, ShaderConstants._FlareData1, ShaderConstants._FlareData2, ShaderConstants._FlareData3, ShaderConstants._FlareData4,
                            false);
                    });

                    return;
                }
            }
        }

        public class PostProcessingFinalSetupPassData
        {
            public TextureHandle destinationTexture;
            public TextureHandle sourceTexture;
            public Material material;
            public RenderingData renderingData;
            public bool isFxaaEnabled;
            public bool doLateFsrColorConversion;
        }

        public void RenderFinalSetup(in TextureHandle source, in TextureHandle destination, ref RenderingData renderingData, bool performFXAA, bool performColorConversion)
        {
            // FSR color onversion or FXAA
            var graph = renderingData.renderGraph;
            UniversalRenderer renderer = (UniversalRenderer)renderingData.cameraData.renderer;
            bool isFxaaEnabled = performFXAA;
            bool doLateFsrColorConversion = performColorConversion;

            using (var builder = graph.AddRenderPass<PostProcessingFinalSetupPassData>("Postprocessing Final Blit Pass", out var passData, ProfilingSampler.Get(URPProfileId.DrawFullscreen)))
            {
                passData.destinationTexture = builder.UseColorBuffer(destination, 0);
                passData.sourceTexture = builder.ReadTexture(source);
                passData.renderingData = renderingData;
                passData.material = m_Materials.scalingSetup;
                passData.isFxaaEnabled = isFxaaEnabled;
                passData.doLateFsrColorConversion = doLateFsrColorConversion;

                // TODO RENDERGRAPH: culling? force culluing off for testing
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PostProcessingFinalSetupPassData data, RenderGraphContext context) =>
                {
                    var cmd = data.renderingData.commandBuffer;
                    var cameraData = data.renderingData.cameraData;
                    var camera = data.renderingData.cameraData.camera;
                    var source = data.sourceTexture;
                    var material = data.material;
                    var isFxaaEnabled = data.isFxaaEnabled;
                    var doLateFsrColorConversion = data.doLateFsrColorConversion;

                    if (isFxaaEnabled)
                    {
                        material.EnableKeyword(ShaderKeywordStrings.Fxaa);
                    }

                    if (doLateFsrColorConversion)
                    {
                        material.EnableKeyword(ShaderKeywordStrings.Gamma20);
                    }

                    cmd.SetGlobalTexture(ShaderPropertyId.sourceTex, source);
                    DrawFullscreenMesh(cmd, material, 0, data.renderingData.cameraData.xr.enabled);
                    //RenderingUtils.ReAllocateIfNeeded(ref m_ScalingSetupTarget, tempRtDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_ScalingSetupTexture");
                });
                return;
            }
        }

        public class PostProcessingFinalFSRScalePassData
        {
            public TextureHandle destinationTexture;
            public TextureHandle sourceTexture;
            public Material material;
            public RenderingData renderingData;

        }

        public void RenderFinalFSRScale(in TextureHandle source, in TextureHandle destination, ref RenderingData renderingData)
        {
            // FSR upscale
            var graph = renderingData.renderGraph;
            UniversalRenderer renderer = (UniversalRenderer)renderingData.cameraData.renderer;
            m_Materials.easu.shaderKeywords = null;

            using (var builder = graph.AddRenderPass<PostProcessingFinalFSRScalePassData>("Postprocessing Final Blit Pass", out var passData, ProfilingSampler.Get(URPProfileId.DrawFullscreen)))
            {
                passData.destinationTexture = builder.UseColorBuffer(destination, 0);
                passData.sourceTexture = builder.ReadTexture(source);
                passData.renderingData = renderingData;
                passData.material = m_Materials.easu;

                // TODO RENDERGRAPH: culling? force culluing off for testing
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PostProcessingFinalFSRScalePassData data, RenderGraphContext context) =>
                {
                    var cmd = data.renderingData.commandBuffer;
                    var cameraData = data.renderingData.cameraData;
                    var camera = data.renderingData.cameraData.camera;
                    var source = data.sourceTexture;
                    var destination = data.destinationTexture;
                    var material = data.material;
                    RTHandle sourceHdl = (RTHandle)source;
                    RTHandle destHdl = (RTHandle)destination;

                    // TODO RENDERGRAPH: dynamic resolution? used scaled size instead?
                    var fsrInputSize = new Vector2(sourceHdl.referenceSize.x, sourceHdl.referenceSize.y);
                    var fsrOutputSize = new Vector2(destHdl.referenceSize.x, destHdl.referenceSize.x);
                    FSRUtils.SetEasuConstants(cmd, fsrInputSize, fsrInputSize, fsrOutputSize);

                    cmd.SetGlobalTexture(ShaderPropertyId.sourceTex, source);
                    DrawFullscreenMesh(cmd, material, 0, data.renderingData.cameraData.xr.enabled);

                    // RCAS
                    // Use the override value if it's available, otherwise use the default.
                    float sharpness = cameraData.fsrOverrideSharpness ? cameraData.fsrSharpness : FSRUtils.kDefaultSharpnessLinear;

                    // Set up the parameters for the RCAS pass unless the sharpness value indicates that it wont have any effect.
                    if (cameraData.fsrSharpness > 0.0f)
                    {
                        // RCAS is performed during the final post blit, but we set up the parameters here for better logical grouping.
                        material.EnableKeyword(ShaderKeywordStrings.Rcas);
                        FSRUtils.SetRcasConstantsLinear(cmd, sharpness);
                    }
                });
                return;
            }
        }

        public class PostProcessingFinalBlitPassData
        {
            public TextureHandle destinationTexture;
            public TextureHandle sourceTexture;
            public Material material;
            public RenderingData renderingData;
            public bool isFxaaEnabled;
        }

        public void RenderFinalBlit(in TextureHandle source, ref RenderingData renderingData, bool performFXAA)
        {
            var graph = renderingData.renderGraph;
            UniversalRenderer renderer = (UniversalRenderer)renderingData.cameraData.renderer;
            bool isFxaaEnabled = performFXAA;

            using (var builder = graph.AddRenderPass<PostProcessingFinalBlitPassData>("Postprocessing Final Blit Pass", out var passData, ProfilingSampler.Get(URPProfileId.DrawFullscreen)))
            {
                passData.destinationTexture = builder.UseColorBuffer(renderer.frameResources.backBufferColor, 0);
                passData.sourceTexture = builder.ReadTexture(source);
                passData.renderingData = renderingData;
                passData.material = m_Materials.finalPass;
                passData.isFxaaEnabled = isFxaaEnabled;

                // TODO RENDERGRAPH: culling? force culluing off for testing
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PostProcessingFinalBlitPassData data, RenderGraphContext context) =>
                {
                    var cmd = data.renderingData.commandBuffer;
                    var cameraData = data.renderingData.cameraData;
                    var camera = data.renderingData.cameraData.camera;
                    var source = data.sourceTexture;
                    var material = data.material;
                    var isFxaaEnabled = data.isFxaaEnabled;

                    if (isFxaaEnabled)
                        material.EnableKeyword(ShaderKeywordStrings.Fxaa);

                    cmd.SetGlobalTexture(ShaderPropertyId.sourceTex, source);

                    // TODO RENDERGRAPH: missing load store op handling
#if ENABLE_VR && ENABLE_XR_MODULE
                    if (cameraData.xr.enabled)
                    {
                        bool yFlip = data.renderingData.cameraData.IsRenderTargetProjectionMatrixFlipped(data.destinationTexture);
                        Vector4 scaleBias = yFlip ? new Vector4(1, -1, 0, 1) : new Vector4(1, 1, 0, 0);

                        cmd.SetViewport(cameraData.pixelRect);
                        cmd.SetGlobalVector(ShaderPropertyId.scaleBias, scaleBias);
                        cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Quads, 4, 1, null);
                    }
                    else
#endif
                    {
                        // TODO RENDERGRAPH: update vertex shader and remove this SetViewProjectionMatrices.
                        cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                        cmd.SetViewport(cameraData.pixelRect);
                        cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, material);
                        cmd.SetViewProjectionMatrices(cameraData.camera.worldToCameraMatrix, cameraData.camera.projectionMatrix);
                    }
                });

                return;
            }
        }

        void RenderFinalPassRenderGraph(CommandBuffer cmd, in TextureHandle source, ref RenderingData renderingData)
        {
            var graph = renderingData.renderGraph;
            ref var cameraData = ref renderingData.cameraData;
            var material = m_Materials.finalPass;
            material.shaderKeywords = null;

            SetupGrain(cameraData, material);
            SetupDithering(cameraData, material);

            if (RequireSRGBConversionBlitToBackBuffer(cameraData))
                material.EnableKeyword(ShaderKeywordStrings.LinearToSRGBConversion);

            GetActiveDebugHandler(renderingData)?.UpdateShaderGlobalPropertiesForFinalValidationPass(cmd, ref cameraData, !m_HasFinalPass);

            bool isFxaaEnabled = (cameraData.antialiasing == AntialiasingMode.FastApproximateAntialiasing);
            bool isFsrEnabled = ((cameraData.imageScalingMode == ImageScalingMode.Upscaling) && (cameraData.upscalingFilter == ImageUpscalingFilter.FSR));
            bool doLateFsrColorConversion = (isFsrEnabled && (isFxaaEnabled || m_hasExternalPostPasses));
            bool isSetupRequired = (isFxaaEnabled || doLateFsrColorConversion);

            var tempRtDesc = cameraData.cameraTargetDescriptor;
            tempRtDesc.msaaSamples = 1;
            tempRtDesc.depthBufferBits = 0;
            var scalingSetupTarget = UniversalRenderer.CreateRenderGraphTexture(graph, tempRtDesc, "scalingSetupTarget", true);
            var upscaleRtDesc = tempRtDesc;
            upscaleRtDesc.width = cameraData.pixelWidth;
            upscaleRtDesc.height = cameraData.pixelHeight;
            var upScaleTarget = UniversalRenderer.CreateRenderGraphTexture(graph, upscaleRtDesc, "_UpscaledTexture", true);

            var currentSource = source;            
            if (isSetupRequired)
            {
                RenderFinalSetup(in currentSource, in scalingSetupTarget, ref renderingData, true, doLateFsrColorConversion);
                currentSource = scalingSetupTarget;
            }

            if (isFsrEnabled)
            {
                RenderFinalFSRScale(in currentSource, in upScaleTarget, ref renderingData);
                currentSource = upScaleTarget;
            }

            RenderFinalBlit(in currentSource, ref renderingData, isFxaaEnabled);
        }
        #endregion
    }
}
