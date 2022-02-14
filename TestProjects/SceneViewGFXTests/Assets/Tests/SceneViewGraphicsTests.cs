using System.Collections;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.Graphics;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Graphics;
using UnityEngine;

public class SceneViewGraphicsTests
{
    private const string referencePath = "Assets/ReferenceImages";

    [UnityTest]
    [UseGraphicsTestCases(referencePath)]
    public IEnumerator SceneViewTests(GraphicsTestCase testCase)
    {
        EditorSceneManager.OpenScene(testCase.ScenePath);
        yield return CaptureSceneView.CaptureFromMainCamera();

        GraphicsTestSettings settings = Object.FindObjectOfType<GraphicsTestSettings>();
        ImageAssert.AreEqual(testCase.ReferenceImage, CaptureSceneView.Result, settings.ImageComparisonSettings);
    }
}