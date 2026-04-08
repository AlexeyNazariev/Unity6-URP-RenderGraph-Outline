using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Experimental.Rendering;

public class OutlineFeature : ScriptableRendererFeature
{
    [Serializable]
    public class OutlineSettings
    {
        public LayerMask LayerMask;
        public Material SilhouetteMaterial;
        public Material OutlineMaterial;
    }

    public OutlineSettings settings = new OutlineSettings();

    // --- ПЕРВЫЙ ПРОХОД: РИСУЕМ СИЛУЭТ (МАСКУ) ---
    class RenderSilhouettePass : ScriptableRenderPass
    {
        private Material silhouetteMaterial;
        private FilteringSettings filteringSettings;
        private static readonly int SilhouetteTextureID = Shader.PropertyToID("_OutlineRenderTexture");

        public RenderSilhouettePass(LayerMask layerMask, Material mat)
        {
            silhouetteMaterial = mat;
            filteringSettings = new FilteringSettings(RenderQueueRange.opaque, layerMask);
        }

        // Класс для передачи данных внутрь графа рендера
        private class PassData
        {
            public RendererListHandle rendererList;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (silhouetteMaterial == null) return;

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();

            // 1. Описываем и создаем временную текстуру для маски
            TextureDesc texDesc = new TextureDesc(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height)
            {
                colorFormat = GraphicsFormat.R8G8B8A8_UNorm,
                clearBuffer = true,
                clearColor = Color.clear, // Очищаем прозрачным цветом
                name = "_OutlineRenderTexture"
            };
            TextureHandle silhouetteTexture = renderGraph.CreateTexture(texDesc);

            // 2. Создаем растровый проход (Raster Pass)
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Render Silhouette", out var passData))
            {
                // Настраиваем, как и что будем рисовать
                var sortingCriteria = cameraData.defaultOpaqueSortFlags;
                var drawingSettings = CreateDrawingSettings(new ShaderTagId("UniversalForward"), renderingData, cameraData, lightData, sortingCriteria);
                drawingSettings.overrideMaterial = silhouetteMaterial; // Подменяем материал на чисто белый

                // Создаем список объектов для рендера
                RendererListParams listParams = new RendererListParams(renderingData.cullResults, drawingSettings, filteringSettings);
                passData.rendererList = renderGraph.CreateRendererList(listParams);
                builder.UseRendererList(passData.rendererList);

                // Указываем, куда рисовать
                builder.SetRenderAttachment(silhouetteTexture, 0, AccessFlags.Write);
                builder.AllowPassCulling(false); // Запрещаем движку пропускать этот проход

                // Регистрируем текстуру глобально, чтобы второй шейдер (контур) мог её прочитать
                builder.SetGlobalTextureAfterPass(silhouetteTexture, SilhouetteTextureID);

                // Выполняем отрисовку
                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    context.cmd.DrawRendererList(data.rendererList);
                });
            }
        }
    }

    // --- ВТОРОЙ ПРОХОД: НАКЛАДЫВАЕМ КОНТУР НА ЭКРАН ---
    class DrawOutlinePass : ScriptableRenderPass
    {
        private Material outlineMaterial;

        public DrawOutlinePass(Material mat)
        {
            outlineMaterial = mat;
            // Указываем, что нам понадобится работать с цветом камеры
            requiresIntermediateTexture = true; 
        }

        private class PassData
        {
            public Material material;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (outlineMaterial == null) return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            TextureHandle activeColorTexture = resourceData.activeColorTexture;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Draw Outline", out var passData))
            {
                passData.material = outlineMaterial;

                // Указываем, что будем рисовать прямо в текущий кадр камеры
                builder.SetRenderAttachment(activeColorTexture, 0);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    // Современный аналог DrawMesh(fullscreenMesh...) — рисуем на весь экран
                    Blitter.BlitTexture(context.cmd, new Vector2(1, 1), data.material, 0);
                });
            }
        }
    }

    private RenderSilhouettePass silhouettePass;
    private DrawOutlinePass outlinePass;

    public override void Create()
    {
        silhouettePass = new RenderSilhouettePass(settings.LayerMask, settings.SilhouetteMaterial)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques
        };

        outlinePass = new DrawOutlinePass(settings.OutlineMaterial)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingSkybox
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings.SilhouetteMaterial != null && settings.OutlineMaterial != null)
        {
            renderer.EnqueuePass(silhouettePass);
            renderer.EnqueuePass(outlinePass);
        }
    }
}