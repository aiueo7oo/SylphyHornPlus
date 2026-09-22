using System;
using SylphyHorn.AppPlacement;
using SylphyHorn.Services.AppPlacement;
using Xunit;

namespace SylphyHorn.Tests
{
	public sealed class PlacementWindowIdentityTests
	{
		private const string Family = "Example_publisher";
		private const string AppId = Family + "!App";

		private static PlacementProcessIdentity Process(uint pid = 10, long created = 100, string path = @"C:\Apps\Editor.exe", string family = null, string id = null)
			=> new PlacementProcessIdentity(pid, created, path, family, id);

		private static PlacementProcessIdentity Host() => Process(path: @"C:\Windows\System32\ApplicationFrameHost.exe");

		private static PlacementProcessIdentity Packaged(uint pid = 20, string id = AppId) => Process(pid, path: @"C:\Packages\Example\App.exe", family: Family, id: id);

		private static PlacementWindowInspection Resolve(
			PlacementProcessIdentity owner,
			string windowId = null,
			string windowClass = "NormalWindow",
			params PlacementProcessIdentity[] children)
			=> PlacementAppIdentityResolver.Resolve(new IntPtr(123), 30, windowClass, owner, windowId, children, id => id == AppId);

		[Fact]
		public void Win32UsesFullPathDespiteCustomDesktopAppId()
		{
			var result = Resolve(Process(), "Custom.Desktop.App");
			Assert.Equal(PlacementInspectionStatus.Ready, result.Status);
			Assert.Equal(PlacementAppKind.ExecutablePath, result.Identity.App.Kind);
			Assert.Equal(@"C:\Apps\Editor.exe", result.Identity.App.Value);
			Assert.False(result.Identity.SameInstance(Resolve(Process(path: @"D:\Other\Editor.exe")).Identity));
		}

		[Theory]
		[InlineData(null)]
		[InlineData(AppId)]
		public void PackagedProcessWorksWithOrWithoutWindowAppId(string windowId)
		{
			var result = Resolve(Packaged(), windowId);
			Assert.Equal(PlacementInspectionStatus.Ready, result.Status);
			Assert.Equal(PlacementAppKind.PackageAppId, result.Identity.App.Kind);
			Assert.Equal(AppId, result.Identity.App.Value);
		}

		[Theory]
		[InlineData("Another_publisher!App")]
		[InlineData(Family + "!Other")]
		[InlineData("Invalid")]
		public void ConflictingPackageEvidenceNeverFallsBackToExecutable(string windowId)
		{
			var result = Resolve(Packaged(), windowId);
			Assert.Equal(PlacementInspectionStatus.Unavailable, result.Status);
			Assert.Null(result.Identity);
		}

		[Fact]
		public void PackageWithoutProcessAppIdRequiresRegisteredWindowIdentity()
		{
			Assert.Equal(PlacementInspectionStatus.Ready, Resolve(Packaged(id: null), AppId).Status);
			Assert.Equal(PlacementInspectionStatus.Unavailable, Resolve(Packaged(id: null), Family + "!Unknown").Status);
			Assert.Equal(PlacementInspectionStatus.NotReady, Resolve(Packaged(id: null)).Status);
		}

		[Fact]
		public void SharedHostCanUseVerifiedFrameIdWithoutChildPid()
		{
			var result = Resolve(Host(), AppId, "ApplicationFrameWindow");
			Assert.Equal(PlacementInspectionStatus.Ready, result.Status);
			Assert.Equal(AppId, result.Identity.App.Value);
			Assert.Null(result.Identity.AppProcess);
			Assert.Equal(PlacementInspectionStatus.Unavailable, Resolve(Host(), Family + "!Unknown", "ApplicationFrameWindow").Status);
			Assert.Equal(PlacementInspectionStatus.NotReady, Resolve(Host(), null, "ApplicationFrameWindow").Status);
		}

		[Fact]
		public void SharedHostUsesOneVerifiedChildButRejectsConflictOrAmbiguity()
		{
			var child = Packaged();
			var result = Resolve(Host(), null, "ApplicationFrameWindow", child);
			Assert.Equal(PlacementInspectionStatus.Ready, result.Status);
			Assert.Same(child, result.Identity.AppProcess);
			Assert.Equal(PlacementInspectionStatus.Unavailable, Resolve(Host(), AppId, "ApplicationFrameWindow", Packaged(id: Family + "!Other")).Status);
			Assert.Equal(PlacementInspectionStatus.Unavailable, Resolve(Host(), null, "ApplicationFrameWindow", child, Packaged(21, Family + "!Other")).Status);
			Assert.Equal(PlacementInspectionStatus.Unavailable, Resolve(Host(), null, "ApplicationFrameWindow", child, Packaged(21)).Status);
			Assert.Equal(PlacementInspectionStatus.Unavailable, Resolve(Host(), AppId, "ApplicationFrameWindow", Process(21)).Status);
			Assert.Equal(PlacementInspectionStatus.Unavailable, Resolve(Host(), AppId, "ApplicationFrameWindow", Process(21, id: AppId)).Status);
		}

		[Fact]
		public void UnrecognizedHostsAndMissingProcessEvidenceAreNotExecutableRules()
		{
			Assert.Equal(PlacementInspectionStatus.Unavailable, Resolve(Host()).Status);
			Assert.Equal(PlacementInspectionStatus.Unavailable, Resolve(Process(), AppId, "ApplicationFrameWindow").Status);
			Assert.Equal(PlacementInspectionStatus.Unavailable, Resolve(Process(path: @"C:\Windows\System32\RuntimeBroker.exe")).Status);
			Assert.Equal(PlacementInspectionStatus.Unavailable, Resolve(Process(created: 0)).Status);
		}

		[Fact]
		public void RevalidationIncludesProcessStartTimeAndHostAppIdentity()
		{
			var first = Resolve(Process()).Identity;
			Assert.True(first.SameInstance(Resolve(Process()).Identity));
			Assert.False(first.SameInstance(Resolve(Process(created: 101)).Identity));
			Assert.False(first.SameInstance(Resolve(Process(pid: 11)).Identity));
			var hosted = Resolve(Host(), AppId, "ApplicationFrameWindow", Packaged()).Identity;
			Assert.False(hosted.SameInstance(Resolve(Host(), AppId, "ApplicationFrameWindow", Packaged(21)).Identity));
			Assert.False(hosted.SameInstance(Resolve(Host(), null, "ApplicationFrameWindow", Packaged(id: Family + "!Other")).Identity));
		}
	}
}
