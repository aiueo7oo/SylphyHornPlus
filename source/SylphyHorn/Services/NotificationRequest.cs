using System;
using System.Windows;
using MetroRadiance.Interop;
using MetroRadiance.Interop.Win32;
using SylphyHorn.Interop;
using SylphyHorn.Properties;
using SylphyHorn.Serialization;
using SylphyHorn.UI.Bindings;

namespace SylphyHorn.Services
{
	internal sealed class NotificationVisualSettings
	{
		internal NotificationVisualSettings(
			uint display,
			WindowPlacement placement,
			int offsetX,
			int offsetY,
			uint windowStyle,
			uint cornerStyle,
			string fontFamily,
			int headerFontSize,
			int bodyFontSize,
			HorizontalAlignment headerAlignment,
			HorizontalAlignment bodyAlignment,
			int lineSpacing,
			bool simpleNotification,
			int notificationMinWidth,
			int simpleNotificationMinWidth,
			int pinWindowMinWidth,
			int notificationMinHeight,
			int pinOffsetX = 0,
			int pinOffsetY = 0)
		{
			this.Display = display;
			this.Placement = placement;
			this.OffsetX = offsetX;
			this.OffsetY = offsetY;
			this.WindowStyle = windowStyle;
			this.CornerStyle = cornerStyle;
			this.FontFamily = fontFamily ?? throw new ArgumentNullException(nameof(fontFamily));
			this.HeaderFontSize = headerFontSize;
			this.BodyFontSize = bodyFontSize;
			this.HeaderAlignment = headerAlignment;
			this.BodyAlignment = bodyAlignment;
			this.HeaderMargin = CreateHeaderMargin(headerAlignment, lineSpacing);
			this.BodyMargin = simpleNotification ? "0,-4,4,0" : "0,0,4,0";
			this.SimpleNotification = simpleNotification;
			this.NotificationMinWidth = notificationMinWidth;
			this.SimpleNotificationMinWidth = simpleNotificationMinWidth;
			this.PinWindowMinWidth = pinWindowMinWidth;
			this.NotificationMinHeight = notificationMinHeight;
			this.PinOffsetX = pinOffsetX;
			this.PinOffsetY = pinOffsetY;
		}

		internal uint Display { get; }
		internal WindowPlacement Placement { get; }
		internal int PinOffsetX { get; }
		internal int PinOffsetY { get; }
		internal int OffsetX { get; }
		internal int OffsetY { get; }
		internal uint WindowStyle { get; }
		internal uint CornerStyle { get; }
		internal string FontFamily { get; }
		internal int HeaderFontSize { get; }
		internal int BodyFontSize { get; }
		internal HorizontalAlignment HeaderAlignment { get; }
		internal HorizontalAlignment BodyAlignment { get; }
		internal string HeaderMargin { get; }
		internal string BodyMargin { get; }
		internal bool SimpleNotification { get; }
		internal int NotificationMinWidth { get; }
		internal int SimpleNotificationMinWidth { get; }
		internal int PinWindowMinWidth { get; }
		internal int NotificationMinHeight { get; }

		internal static NotificationVisualSettings Capture(GeneralSettings settings)
		{
			if (settings == null) throw new ArgumentNullException(nameof(settings));
			var fontFamily = settings.NotificationFontFamily.Value;
			var defaultFont = GeneralSettings.NotificationFontFamilyDefaultValue;
			var resolvedFontFamily = !string.IsNullOrEmpty(fontFamily)
				? fontFamily + ", " + defaultFont
				: defaultFont;

			return new NotificationVisualSettings(
				display: settings.Display.Value,
				placement: (WindowPlacement)settings.Placement.Value,
				offsetX: settings.NotificationOffsetX,
				offsetY: settings.NotificationOffsetY,
				windowStyle: settings.NotificationWindowStyle.Value,
				cornerStyle: settings.NotificationCornerStyle.Value,
				fontFamily: resolvedFontFamily,
				headerFontSize: settings.NotificationHeaderFontSize,
				bodyFontSize: settings.NotificationBodyFontSize,
				headerAlignment: (HorizontalAlignment)settings.NotificationHeaderAlignment.Value,
				bodyAlignment: (HorizontalAlignment)settings.NotificationBodyAlignment.Value,
				lineSpacing: settings.NotificationLineSpacing.Value,
				simpleNotification: settings.SimpleNotification,
				notificationMinWidth: settings.NotificationMinWidth,
				simpleNotificationMinWidth: settings.SimpleNotificationMinWidth,
				pinWindowMinWidth: settings.PinWindowMinWidth,
				notificationMinHeight: settings.NotificationMinHeight,
				pinOffsetX: settings.PinWindowOffsetX,
				pinOffsetY: settings.PinWindowOffsetY);
		}

		private static string CreateHeaderMargin(HorizontalAlignment alignment, int lineSpacing)
		{
			if (alignment == HorizontalAlignment.Left) return $"2,0,0,{lineSpacing}";
			if (alignment == HorizontalAlignment.Right) return $"0,0,6,{lineSpacing}";
			return $"0,0,0,{lineSpacing}";
		}
	}

	internal sealed class NotificationSettingsSnapshot
	{
		internal NotificationSettingsSnapshot(
			bool notificationWhenSwitchedDesktop,
			bool alwaysShowDesktopNotification,
			bool useDesktopName,
			int duration,
			NotificationVisualSettings visual)
		{
			this.NotificationWhenSwitchedDesktop = notificationWhenSwitchedDesktop;
			this.AlwaysShowDesktopNotification = alwaysShowDesktopNotification;
			this.UseDesktopName = useDesktopName;
			this.Duration = duration;
			this.Visual = visual ?? throw new ArgumentNullException(nameof(visual));
		}

		internal bool NotificationWhenSwitchedDesktop { get; }
		internal bool AlwaysShowDesktopNotification { get; }
		internal bool UseDesktopName { get; }
		internal int Duration { get; }
		internal NotificationVisualSettings Visual { get; }

		internal static NotificationSettingsSnapshot Capture(GeneralSettings settings)
		{
			if (settings == null) throw new ArgumentNullException(nameof(settings));
			return new NotificationSettingsSnapshot(
				settings.NotificationWhenSwitchedDesktop,
				settings.AlwaysShowDesktopNotification,
				settings.UseDesktopName,
				settings.NotificationDuration,
				NotificationVisualSettings.Capture(settings));
		}
	}

	internal sealed class DesktopNotificationRequest
	{
		internal DesktopNotificationRequest(
			string title,
			string header,
			string body,
			string residentHeader,
			int duration,
			bool resident,
			NotificationVisualSettings visual)
		{
			this.Title = title ?? throw new ArgumentNullException(nameof(title));
			this.Header = header ?? throw new ArgumentNullException(nameof(header));
			this.Body = body ?? throw new ArgumentNullException(nameof(body));
			this.ResidentHeader = residentHeader ?? throw new ArgumentNullException(nameof(residentHeader));
			this.Duration = duration;
			this.Resident = resident;
			this.Visual = visual ?? throw new ArgumentNullException(nameof(visual));
		}

		internal string Title { get; }
		internal string Header { get; }
		internal string Body { get; }
		internal string ResidentHeader { get; }
		internal int Duration { get; }
		internal bool Resident { get; }
		internal NotificationVisualSettings Visual { get; }
	}

	internal sealed class PinTargetGeometry
	{
		internal PinTargetGeometry(int left, int top, int width, int height, double dpiScaleX, double dpiScaleY)
		{
			this.Left = left;
			this.Top = top;
			this.Width = width;
			this.Height = height;
			this.DpiScaleX = dpiScaleX;
			this.DpiScaleY = dpiScaleY;
		}

		internal int Left { get; }
		internal int Top { get; }
		internal int Width { get; }
		internal int Height { get; }
		internal double DpiScaleX { get; }
		internal double DpiScaleY { get; }
	}

	internal sealed class PinNotificationRequest
	{
		internal PinNotificationRequest(
			string title,
			string header,
			string body,
			int duration,
			PinOperations operation,
			PinTargetGeometry geometry,
			NotificationVisualSettings visual)
		{
			this.Title = title ?? throw new ArgumentNullException(nameof(title));
			this.Header = header ?? throw new ArgumentNullException(nameof(header));
			this.Body = body ?? throw new ArgumentNullException(nameof(body));
			this.Duration = duration;
			this.Operation = operation;
			this.Geometry = geometry;
			this.Visual = visual ?? throw new ArgumentNullException(nameof(visual));
		}

		internal string Title { get; }
		internal string Header { get; }
		internal string Body { get; }
		internal int Duration { get; }
		internal PinOperations Operation { get; }
		internal PinTargetGeometry Geometry { get; }
		internal NotificationVisualSettings Visual { get; }
	}

	internal static class NotificationRequestMaterializer
	{
		internal static DesktopNotificationRequest CreateCurrent(int number, string name, NotificationSettingsSnapshot settings)
			=> CreateDesktop(number, name, NotificationTextFormatter.CreateResidentHeader(settings.Visual.SimpleNotification), moved: false, settings);

		internal static DesktopNotificationRequest CreateSwitched(int number, string name, NotificationSettingsSnapshot settings)
			=> CreateDesktop(number, name, NotificationTextFormatter.CreateSwitchedHeader(settings.Visual.SimpleNotification), moved: false, settings);

		internal static DesktopNotificationRequest CreateMoved(int currentNumber, string name, int oldNumber, int newNumber, NotificationSettingsSnapshot settings)
			=> CreateDesktop(
				currentNumber,
				name,
				NotificationTextFormatter.CreateMovedHeader(oldNumber, newNumber, settings.Visual.SimpleNotification),
				moved: true,
				settings);

		internal static PinNotificationRequest CreatePin(PinOperations operation, PinTargetGeometry geometry, NotificationSettingsSnapshot settings)
		{
			if (settings == null) throw new ArgumentNullException(nameof(settings));
			return new PinNotificationRequest(
				ProductInfo.Title,
				NotificationTextFormatter.CreatePinHeader(settings.Visual.SimpleNotification),
				NotificationTextFormatter.CreatePinBody(operation, settings.Visual.SimpleNotification),
				settings.Duration,
				operation,
				geometry,
				settings.Visual);
		}

		internal static PinTargetGeometry CapturePinGeometry(IntPtr target)
		{
			RECT rect;
			if (!NativeMethods.GetWindowRect(target, out rect)) return null;
			var dpi = PerMonitorDpi.GetDpi(target);
			return new PinTargetGeometry(
				rect.Left,
				rect.Top,
				rect.Right - rect.Left,
				rect.Bottom - rect.Top,
				dpi.ScaleX,
				dpi.ScaleY);
		}

		private static DesktopNotificationRequest CreateDesktop(
			int number,
			string name,
			string header,
			bool moved,
			NotificationSettingsSnapshot settings)
		{
			if (settings == null) throw new ArgumentNullException(nameof(settings));
			return new DesktopNotificationRequest(
				ProductInfo.Title,
				header,
				NotificationTextFormatter.CreateDesktopBody(
					number,
					name,
					settings.UseDesktopName,
					settings.Visual.SimpleNotification,
					moved),
				NotificationTextFormatter.CreateResidentHeader(settings.Visual.SimpleNotification),
				settings.Duration,
				settings.AlwaysShowDesktopNotification,
				settings.Visual);
		}
	}
}
