using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Graphics;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

[ExecuteAlways]
public class HDRP_TestSettings : GraphicsTestSettings
{
    public UnityEngine.Events.UnityEvent doBeforeTest;
    public int captureFramerate = 0;
    public int waitFrames = 0;
    public bool xrCompatible = true;

    [UnityEngine.Range(1.0f, 10.0f)]
    public float xrThresholdMultiplier = 1.0f;

    public bool checkMemoryAllocation = true;

    public RenderPipelineAsset renderPipelineAsset;

    [Tooltip("RP Asset change is only effective after a frame is render")]
    public bool forceCameraRenderDuringSetup = false;
    bool render = false;

    void Awake()
    {
        if (renderPipelineAsset == null)
        {
            Debug.LogWarning("No RenderPipelineAsset has been assigned in the test settings. This may result in a wrong test.");
            return;
        }

        var currentRP = GraphicsSettings.renderPipelineAsset;

        render = false;
        if (currentRP != renderPipelineAsset)
        {
            quitDebug.AppendLine($"{SceneManager.GetActiveScene().name} RP asset change: {((currentRP == null) ? "null" : currentRP.name)} => {renderPipelineAsset.name}");

            GraphicsSettings.renderPipelineAsset = renderPipelineAsset;
            render = forceCameraRenderDuringSetup;
        }
    }

    private void OnEnable()
    {
        // Render pipeline is only reconstructed when a frame is renderer
        // If scene requires lightmap baking, we have to force it
        // Currently Camera.Render() fails on mac so we have to filter out the tests that rely on forceCameraRenderDuringSetup (like 2120 for APV).
        // But since setup is run regardless of the filter we add this explicit check on platform
        if (render && !Application.isPlaying)
        {
            try
            {
                LogAssert.Expect(LogType.Error, "Metal: Error creating pipeline state (Hidden/HDRP/CopyDepthBuffer): depthAttachmentPixelFormat is not valid and shader writes to depth");
            }
            catch (System.InvalidOperationException) // thrown if there is no logscope
            { }
            Camera.main.Render();
        }
    }

    static StringBuilder quitDebug = new StringBuilder();

    void OnApplicationQuit()
    {
        if (quitDebug.Length == 0) return;

        Debug.Log($"Scenes that needed to change the RP asset:{Environment.NewLine}{quitDebug.ToString()}");

        quitDebug.Clear();
    }
}
