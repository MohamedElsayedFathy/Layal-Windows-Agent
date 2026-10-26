using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class GltfSkinnedAnimationRendererTests
{
    [TestMethod]
    public void BundledGltfPreviewContainsARealSkinnedMeshAndAnimation()
    {
        var asset = GltfSkinnedAnimationRenderer.InspectBundledAsset();

        Assert.AreEqual(22, asset.NodeCount, "Unexpected node count in the vetted Rigged Figure GLB.");
        Assert.AreEqual(370, asset.VertexCount);
        Assert.AreEqual(256, asset.TriangleCount);
        Assert.AreEqual(19, asset.JointCount);
        Assert.AreEqual(1, asset.AnimationCount);
        Assert.AreEqual(57, asset.AnimationChannelCount);
    }

    [TestMethod]
    public void BundledGltfAnimationChangesTheSkinnedVertexPose()
    {
        var asset = GltfSkinnedAnimationRenderer.InspectBundledAsset();

        Assert.IsTrue(
            asset.MaximumAnimatedVertexDisplacement > 0.001,
            $"The animation was parsed but did not deform the mesh enough to prove skinning. Maximum displacement: {asset.MaximumAnimatedVertexDisplacement:0.000000}");
    }
}
