using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using SylphyHorn.Services;
using SylphyHorn.UI.Bindings;
using Xunit;

namespace SylphyHorn.Tests
{
	public sealed class NotificationRequestMaterializerTests
	{
		[Fact]
		public void MonitorSelectionFallsBackOnlyWhenNeededAndHandlesAnEmptyTopology()
		{
			var left = new Rect(-1920, 0, 1920, 1040);
			var right = new Rect(0, 0, 1920, 1040);
			var monitors = new[] { new Monitor("Left", left, left), new Monitor("Right", right, right) };
			var cursorReads = 0;
			Func<Rect> current = () => { cursorReads++; return left; };

			Assert.Equal(right, Assert.Single(NotificationWindowFactory.ResolveSwitchAreas(2, monitors, current)));
			Assert.Equal(new[] { left, right }, NotificationWindowFactory.ResolveSwitchAreas(uint.MaxValue, monitors, current));
			Assert.Equal(0, cursorReads);
			Assert.Equal(left, Assert.Single(NotificationWindowFactory.ResolveSwitchAreas(0, monitors, current)));
			Assert.Equal(left, Assert.Single(NotificationWindowFactory.ResolveSwitchAreas(3, monitors, current)));
			Assert.Equal(2, cursorReads);
			Assert.Empty(NotificationWindowFactory.ResolveSwitchAreas(1, Array.Empty<Monitor>(), current));
			Assert.Equal(2, cursorReads);
		}

		[Fact]
		public void SwitchedRequestMaterializesTextLifetimeAndEveryVisualSetting()
		{
			var settings = CreateSettings(alwaysShow: true, simple: false, useDesktopName: true);

			var request = NotificationRequestMaterializer.CreateSwitched(3, "Work", settings);

			Assert.Equal("SylphyHornPlus", request.Title);
			Assert.Equal("Virtual Desktop Switched", request.Header);
			Assert.Equal("Desktop 3: Work", request.Body);
			Assert.Equal("Virtual Desktop", request.ResidentHeader);
			Assert.Equal(4321, request.Duration);
			Assert.True(request.Resident);
			AssertVisualSettings(request.Visual, simple: false);
		}

		[Fact]
		public void MovedRequestMaterializesOldToNewOrderAndMovedBody()
		{
			var request = NotificationRequestMaterializer.CreateMoved(
				4,
				"Moved",
				2,
				5,
				CreateSettings(alwaysShow: false, simple: false, useDesktopName: true));

			Assert.Equal("Desktop 2 Moved to Desktop 5", request.Header);
			Assert.Equal("Reordered Desktop 4: Moved", request.Body);
			Assert.False(request.Resident);
		}

		[Theory]
		[InlineData(null, false, "Current Desktop: Desktop 7")]
		[InlineData("", false, "Current Desktop: Desktop 7")]
		[InlineData("Named", false, "Desktop 7: Named")]
		[InlineData(null, true, "Desktop 7")]
		[InlineData("", true, "Desktop 7")]
		[InlineData("Named", true, "7. Named")]
		public void SwitchedRequestPreservesNullEmptyAndNamedDesktopContracts(string name, bool simple, string expectedBody)
		{
			var request = NotificationRequestMaterializer.CreateSwitched(
				7,
				name,
				CreateSettings(alwaysShow: false, simple: simple, useDesktopName: true));

			Assert.Equal(expectedBody, request.Body);
			Assert.Equal(simple ? "" : "Virtual Desktop Switched", request.Header);
		}

		[Theory]
		[InlineData(false, false, "Current Desktop: Desktop 6")]
		[InlineData(true, false, "Desktop 6: Ignored")]
		[InlineData(false, true, "Desktop 6")]
		[InlineData(true, true, "6. Ignored")]
		public void SimpleNotificationAndUseDesktopNameAreMaterializedIndependently(bool useDesktopName, bool simple, string expectedBody)
		{
			var request = NotificationRequestMaterializer.CreateSwitched(
				6,
				"Ignored",
				CreateSettings(alwaysShow: false, simple: simple, useDesktopName: useDesktopName));

			Assert.Equal(expectedBody, request.Body);
			Assert.Equal(simple, request.Visual.SimpleNotification);
		}

		[Fact]
		public void ShowCurrentDesktopRequestUsesResidentHeaderAndSnapshotValues()
		{
			var request = NotificationRequestMaterializer.CreateCurrent(
				2,
				"Current",
				CreateSettings(alwaysShow: true, simple: false, useDesktopName: true));

			Assert.Equal("Virtual Desktop", request.Header);
			Assert.Equal("Desktop 2: Current", request.Body);
			Assert.Equal("Virtual Desktop", request.ResidentHeader);
			Assert.True(request.Resident);
			Assert.Equal(4321, request.Duration);
		}

		[Theory]
		[InlineData((int)PinOperations.PinWindow, "Pinned this window")]
		[InlineData((int)PinOperations.UnpinWindow, "Unpinned this window")]
		public void PinAndUnpinRequestsMaterializeOperationTextAndGeometry(int operationValue, string expectedBody)
		{
			var geometry = new PinTargetGeometry(101, 202, 803, 604, 1.25, 1.5);
			var operation = (PinOperations)operationValue;

			var request = NotificationRequestMaterializer.CreatePin(
				operation,
				geometry,
				CreateSettings(alwaysShow: false, simple: false, useDesktopName: false));

			Assert.Equal("SylphyHornPlus", request.Title);
			Assert.Equal("Virtual Desktop", request.Header);
			Assert.Equal(expectedBody, request.Body);
			Assert.Equal(4321, request.Duration);
			Assert.Equal(operation, request.Operation);
			Assert.Same(geometry, request.Geometry);
			Assert.Equal(101, request.Geometry.Left);
			Assert.Equal(202, request.Geometry.Top);
			Assert.Equal(803, request.Geometry.Width);
			Assert.Equal(604, request.Geometry.Height);
			Assert.Equal(1.25, request.Geometry.DpiScaleX);
			Assert.Equal(1.5, request.Geometry.DpiScaleY);
		}

		[Fact]
		public void PinRequestSupportsFailedGeometrySnapshotWithoutRetainingTargetHandle()
		{
			var request = NotificationRequestMaterializer.CreatePin(
				PinOperations.PinApp,
				null,
				CreateSettings(alwaysShow: false, simple: true, useDesktopName: false));

			Assert.Null(request.Geometry);
			Assert.Equal("", request.Header);
			Assert.Equal("Application Pinned", request.Body);
			Assert.DoesNotContain(
				typeof(PinNotificationRequest).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
				field => field.FieldType == typeof(IntPtr));
		}

		[Fact]
		public void RequestsRetainOnlyImmutableDataAndNoUiSettingsOrDispatcherObjects()
		{
			var visual = CreateSettings(alwaysShow: true, simple: false, useDesktopName: true);
			var requests = new object[]
			{
				NotificationRequestMaterializer.CreateSwitched(1, "One", visual),
				NotificationRequestMaterializer.CreatePin(
					PinOperations.PinWindow,
					new PinTargetGeometry(1, 2, 3, 4, 1.0, 1.0),
					visual),
			};

			foreach (var request in requests) AssertContainsOnlyRequestData(request);
		}

		[Fact]
		public void RequestGraphTypesAreSealedAndAllInstanceFieldsAreReadonly()
		{
			var types = new[]
			{
				typeof(DesktopNotificationRequest),
				typeof(PinNotificationRequest),
				typeof(PinTargetGeometry),
				typeof(NotificationVisualSettings),
			};

			foreach (var type in types)
			{
				Assert.True(type.IsSealed, type.FullName);
				Assert.All(
					type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
					field => Assert.True(field.IsInitOnly, $"{type.FullName}.{field.Name}"));
			}
		}

		[Fact]
		public void ToggleHostBoundaryAcceptsMaterializedRequestAndNoDelegate()
		{
			var method = typeof(INotificationHost).GetMethod(nameof(INotificationHost.EnqueueToggle));

			var parameter = Assert.Single(method.GetParameters());
			Assert.Equal(typeof(DesktopNotificationRequest), parameter.ParameterType);
			Assert.False(typeof(Delegate).IsAssignableFrom(parameter.ParameterType));
		}

		private static NotificationSettingsSnapshot CreateSettings(bool alwaysShow, bool simple, bool useDesktopName)
			=> new NotificationSettingsSnapshot(
				notificationWhenSwitchedDesktop: true,
				alwaysShowDesktopNotification: alwaysShow,
				useDesktopName: useDesktopName,
				duration: 4321,
				visual: new NotificationVisualSettings(
					display: uint.MaxValue,
					placement: WindowPlacement.BottomRight,
					offsetX: 17,
					offsetY: -23,
					windowStyle: 4,
					cornerStyle: 2,
					fontFamily: "Test Font, Fallback Font",
					headerFontSize: 19,
					bodyFontSize: 31,
					headerAlignment: HorizontalAlignment.Right,
					bodyAlignment: HorizontalAlignment.Center,
					lineSpacing: -7,
					simpleNotification: simple,
					notificationMinWidth: 501,
					simpleNotificationMinWidth: 211,
					pinWindowMinWidth: 401,
					notificationMinHeight: 101));

		private static void AssertVisualSettings(NotificationVisualSettings visual, bool simple)
		{
			Assert.Equal(uint.MaxValue, visual.Display);
			Assert.Equal(WindowPlacement.BottomRight, visual.Placement);
			Assert.Equal(17, visual.OffsetX);
			Assert.Equal(-23, visual.OffsetY);
			Assert.Equal(4u, visual.WindowStyle);
			Assert.Equal(2u, visual.CornerStyle);
			Assert.Equal("Test Font, Fallback Font", visual.FontFamily);
			Assert.Equal(19, visual.HeaderFontSize);
			Assert.Equal(31, visual.BodyFontSize);
			Assert.Equal(HorizontalAlignment.Right, visual.HeaderAlignment);
			Assert.Equal(HorizontalAlignment.Center, visual.BodyAlignment);
			Assert.Equal("0,0,6,-7", visual.HeaderMargin);
			Assert.Equal(simple ? "0,-4,4,0" : "0,0,4,0", visual.BodyMargin);
			Assert.Equal(simple, visual.SimpleNotification);
			Assert.Equal(501, visual.NotificationMinWidth);
			Assert.Equal(211, visual.SimpleNotificationMinWidth);
			Assert.Equal(401, visual.PinWindowMinWidth);
			Assert.Equal(101, visual.NotificationMinHeight);
		}

		private static void AssertContainsOnlyRequestData(object root)
		{
			var pending = new Stack<object>();
			var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
			pending.Push(root);

			while (pending.Count > 0)
			{
				var value = pending.Pop();
				if (value == null || !visited.Add(value)) continue;
				var type = value.GetType();
				Assert.False(typeof(Window).IsAssignableFrom(type), type.FullName);
				Assert.False(typeof(DependencyObject).IsAssignableFrom(type), type.FullName);
				Assert.False(typeof(Dispatcher).IsAssignableFrom(type), type.FullName);
				Assert.False(typeof(Delegate).IsAssignableFrom(type), type.FullName);
				Assert.DoesNotContain("SerializableProperty", type.FullName ?? "");
				Assert.DoesNotContain("GeneralSettings", type.FullName ?? "");
				Assert.DoesNotContain("DesktopRuntimeState", type.FullName ?? "");

				if (type.IsPrimitive || type.IsEnum || value is string || value is decimal) continue;
				foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
					Assert.NotEqual(typeof(IntPtr), field.FieldType);
					pending.Push(field.GetValue(value));
				}
			}
		}

		private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
		{
			internal static ReferenceEqualityComparer Instance { get; } = new ReferenceEqualityComparer();

			public new bool Equals(object x, object y) => ReferenceEquals(x, y);

			public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
		}
	}
}
