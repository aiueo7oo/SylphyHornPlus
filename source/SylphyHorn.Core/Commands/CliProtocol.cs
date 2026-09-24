using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SylphyHorn.Commands
{
	[DataContract]
	internal sealed class CliRequest
	{
		[DataMember(Name = "schemaVersion", IsRequired = true)]
		public int SchemaVersion = 1;

		[DataMember(Name = "args", IsRequired = true)]
		public string[] Args;
	}

	[DataContract]
	internal sealed class CliResponse
	{
		[DataMember(Name = "schemaVersion", Order = 0)]
		public int SchemaVersion = 1;

		[DataMember(Name = "command", Order = 1)]
		public string Command;

		[DataMember(Name = "success", Order = 2)]
		public bool Success;

		[DataMember(Name = "data", Order = 3, EmitDefaultValue = false)]
		public CliData Data;

		[DataMember(Name = "error", Order = 4, EmitDefaultValue = false)]
		public CliError Error;

		internal int ExitCode => this.Success ? 0 : this.Error.Code == "invalid_arguments" ? 2
			: this.Error.Code == "host_unavailable" ? 3 : this.Error.Code == "result_unconfirmed" ? 5 : 4;

		internal static CliResponse Ok(string command, CliData data) => new CliResponse { Command = command, Success = true, Data = data };

		internal static CliResponse Fail(string command, string code, string message, bool retryable = false)
			=> new CliResponse { Command = command, Error = new CliError { Code = code, Message = message, Retryable = retryable } };
	}

	[DataContract]
	internal sealed class CliError
	{
		[DataMember(Name = "results", Order = 3, EmitDefaultValue = false)]
		public CliAssignmentResult[] Results;

		[DataMember(Name = "code", Order = 0)]
		public string Code;

		[DataMember(Name = "message", Order = 1)]
		public string Message;

		[DataMember(Name = "retryable", Order = 2)]
		public bool Retryable;
	}

	[DataContract]
	internal sealed class CliData
	{
		[DataMember(Name = "perDesktopWallpaper", EmitDefaultValue = false)]
		public bool? PerDesktopWallpaper;

		[DataMember(Name = "overrideOnStartup", EmitDefaultValue = false)]
		public bool? OverrideOnStartup;

		[DataMember(Name = "nativeWallpaperSupported", EmitDefaultValue = false)]
		public bool? NativeWallpaperSupported;

		[DataMember(Name = "wallpaperEnabled", EmitDefaultValue = false)]
		public bool? WallpaperEnabled;

		[DataMember(Name = "monitor", EmitDefaultValue = false)]
		public string Monitor;

		[DataMember(Name = "placement", EmitDefaultValue = false)]
		public string Placement;

		[DataMember(Name = "offsetX", EmitDefaultValue = false)]
		public int? OffsetX;

		[DataMember(Name = "offsetY", EmitDefaultValue = false)]
		public int? OffsetY;

		[DataMember(Name = "minWidth", EmitDefaultValue = false)]
		public int? MinWidth;

		[DataMember(Name = "simpleMinWidth", EmitDefaultValue = false)]
		public int? SimpleMinWidth;

		[DataMember(Name = "minHeight", EmitDefaultValue = false)]
		public int? MinHeight;

		[DataMember(Name = "pinMinWidth", EmitDefaultValue = false)]
		public int? PinMinWidth;

		[DataMember(Name = "pinOffsetX", EmitDefaultValue = false)]
		public int? PinOffsetX;

		[DataMember(Name = "pinOffsetY", EmitDefaultValue = false)]
		public int? PinOffsetY;

		[DataMember(Name = "monitorAvailable", EmitDefaultValue = false)]
		public bool? MonitorAvailable;

		[DataMember(Name = "monitors", EmitDefaultValue = false)]
		public CliMonitor[] Monitors;

		[DataMember(Name = "simple", EmitDefaultValue = false)]
		public bool? Simple;

		[DataMember(Name = "useDesktopName", EmitDefaultValue = false)]
		public bool? UseDesktopName;

		[DataMember(Name = "theme", EmitDefaultValue = false)]
		public string Theme;

		[DataMember(Name = "corners", EmitDefaultValue = false)]
		public string Corners;

		[DataMember(Name = "fontFamily", EmitDefaultValue = false)]
		public string FontFamily;

		[DataMember(Name = "headerFontSize", EmitDefaultValue = false)]
		public int? HeaderFontSize;

		[DataMember(Name = "bodyFontSize", EmitDefaultValue = false)]
		public int? BodyFontSize;

		[DataMember(Name = "headerAlign", EmitDefaultValue = false)]
		public string HeaderAlign;

		[DataMember(Name = "bodyAlign", EmitDefaultValue = false)]
		public string BodyAlign;

		[DataMember(Name = "lineSpacing", EmitDefaultValue = false)]
		public int? LineSpacing;

		[DataMember(Name = "cornersSupported", EmitDefaultValue = false)]
		public bool? CornersSupported;

		[DataMember(Name = "loop", EmitDefaultValue = false)]
		public bool? Loop;

		[DataMember(Name = "overrideWindowsShortcuts", EmitDefaultValue = false)]
		public bool? OverrideWindowsShortcuts;

		[DataMember(Name = "onSwitch", EmitDefaultValue = false)]
		public bool? OnSwitch;

		[DataMember(Name = "alwaysShow", EmitDefaultValue = false)]
		public bool? AlwaysShow;

		[DataMember(Name = "durationMs", EmitDefaultValue = false)]
		public int? DurationMs;

		[DataMember(Name = "showDesktop", EmitDefaultValue = false)]
		public bool? ShowDesktop;

		[DataMember(Name = "currentNumberOnly", EmitDefaultValue = false)]
		public bool? CurrentNumberOnly;

		[DataMember(Name = "language", EmitDefaultValue = false)]
		public string Language;

		[DataMember(Name = "restartRequired", EmitDefaultValue = false)]
		public bool? RestartRequired;

		[DataMember(Name = "apps", EmitDefaultValue = false)]
		public CliApp[] Apps;

		[DataMember(Name = "source", EmitDefaultValue = false)]
		public string Source;

		[DataMember(Name = "assignmentStatus", EmitDefaultValue = false)]
		public string AssignmentStatus;

		[DataMember(Name = "createMissingDesktops", EmitDefaultValue = false)]
		public bool? CreateMissingDesktops;

		[DataMember(Name = "closeCreatedDesktops", EmitDefaultValue = false)]
		public bool? CloseCreatedDesktops;

		[DataMember(Name = "closingTargets", EmitDefaultValue = false)]
		public CliAssignmentTarget[] ClosingTargets;

		[DataMember(Name = "results", EmitDefaultValue = false)]
		public CliAssignmentResult[] Results;

		[DataMember(Name = "dryRun", EmitDefaultValue = false)]
		public bool? DryRun;

		[DataMember(Name = "assignmentEnabled", EmitDefaultValue = false)]
		public bool? AssignmentEnabled;

		[DataMember(Name = "assignments", EmitDefaultValue = false)]
		public CliAssignment[] Assignments;

		[DataMember(Name = "changed", EmitDefaultValue = false)]
		public bool? Changed;

		[DataMember(Name = "desktop", EmitDefaultValue = false)]
		public CliDesktop Desktop;

		[DataMember(Name = "desktops", EmitDefaultValue = false)]
		public CliDesktop[] Desktops;

		[DataMember(Name = "window", EmitDefaultValue = false)]
		public CliWindow Window;

		[DataMember(Name = "windows", EmitDefaultValue = false)]
		public CliWindow[] Windows;

		[DataMember(Name = "complete", EmitDefaultValue = false)]
		public bool? Complete;

		[DataMember(Name = "unavailableCount", EmitDefaultValue = false)]
		public int? UnavailableCount;
	}

	[DataContract]
	internal sealed class CliMonitor
	{
		[DataMember(Name = "number")]
		public int Number;

		[DataMember(Name = "name")]
		public string Name;

		[DataMember(Name = "bounds")]
		public CliRectangle Bounds;

		[DataMember(Name = "workArea")]
		public CliRectangle WorkArea;
	}

	[DataContract]
	internal sealed class CliRectangle
	{
		[DataMember(Name = "x")]
		public double X;

		[DataMember(Name = "y")]
		public double Y;

		[DataMember(Name = "width")]
		public double Width;

		[DataMember(Name = "height")]
		public double Height;
	}

	[DataContract]
	internal sealed class CliApp
	{
		[DataMember(Name = "displayName")]
		public string DisplayName;

		[DataMember(Name = "executablePath")]
		public string ExecutablePath;

		[DataMember(Name = "appKind")]
		public string AppKind;

		[DataMember(Name = "appIdentity")]
		public string AppIdentity;

		[DataMember(Name = "canAssign")]
		public bool CanAssign;

		[DataMember(Name = "reason")]
		public string Reason;
	}

	[DataContract]
	internal sealed class CliAssignmentTarget
	{
		[DataMember(Name = "desktopName", EmitDefaultValue = false)]
		public string DesktopName;

		[DataMember(Name = "desktopNumber", EmitDefaultValue = false)]
		public int? DesktopNumber;
	}

	[DataContract]
	internal sealed class CliAssignment
	{
		[DataMember(Name = "id")]
		public string Id;

		[DataMember(Name = "enabled")]
		public bool Enabled;

		[DataMember(Name = "appKind")]
		public string AppKind;

		[DataMember(Name = "appIdentity")]
		public string AppIdentity;

		[DataMember(Name = "executablePath")]
		public string ExecutablePath;

		[DataMember(Name = "displayName")]
		public string DisplayName;

		[DataMember(Name = "desktopName", EmitDefaultValue = false)]
		public string DesktopName;

		[DataMember(Name = "desktopNumber", EmitDefaultValue = false)]
		public int? DesktopNumber;
	}

	[DataContract]
	internal sealed class CliAssignmentResult
	{
		[DataMember(Name = "windowId")]
		public string WindowId;

		[DataMember(Name = "ruleId")]
		public string RuleId;

		[DataMember(Name = "title")]
		public string Title;

		[DataMember(Name = "sourceDesktopId")]
		public string SourceDesktopId;

		[DataMember(Name = "targetDesktopId")]
		public string TargetDesktopId;

		[DataMember(Name = "canApply")]
		public bool CanApply;

		[DataMember(Name = "outcome")]
		public string Outcome;
	}

	[DataContract]
	internal sealed class CliDesktop
	{
		[DataMember(Name = "id")]
		public string Id;

		[DataMember(Name = "number")]
		public int Number;

		[DataMember(Name = "name")]
		public string Name;

		[DataMember(Name = "nameAvailable")]
		public bool NameAvailable;

		[DataMember(Name = "wallpaperPath")]
		public string WallpaperPath;

		[DataMember(Name = "wallpaperPathAvailable")]
		public bool WallpaperPathAvailable;

		[DataMember(Name = "wallpaperPathConfirmed")]
		public bool WallpaperPathConfirmed;

		[DataMember(Name = "wallpaperPosition")]
		public string WallpaperPosition;

		[DataMember(Name = "current")]
		public bool Current;
	}

	[DataContract]
	internal sealed class CliWindow
	{
		[DataMember(Name = "id")]
		public string Id;

		[DataMember(Name = "title")]
		public string Title;

		[DataMember(Name = "processId")]
		public uint ProcessId;

		[DataMember(Name = "processName")]
		public string ProcessName;

		[DataMember(Name = "executablePath")]
		public string ExecutablePath;

		[DataMember(Name = "appId")]
		public string AppId;

		[DataMember(Name = "desktopId")]
		public string DesktopId;

		[DataMember(Name = "pinned")]
		public bool Pinned;

		[DataMember(Name = "windowPinned", EmitDefaultValue = false)]
		public bool? WindowPinned;

		[DataMember(Name = "appPinned", EmitDefaultValue = false)]
		public bool? AppPinned;

		[DataMember(Name = "movable")]
		public bool Movable;
	}

	internal static class CliProtocol
	{
		internal const int MaximumFrameBytes = 1024 * 1024;

		internal static string PipeName(string company, string product)
		{
			using (var identity = WindowsIdentity.GetCurrent())
			using (var hash = SHA256.Create())
			using (var process = Process.GetCurrentProcess())
			{
				var key = company + "\r\n" + product + "\r\n" + identity.User.Value + "\r\n" + process.SessionId;
				return "SylphyHorn.Cli.v1." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "");
			}
		}

		internal static byte[] Serialize<T>(T value)
		{
			using (var stream = new MemoryStream())
			{
				new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
				return stream.ToArray();
			}
		}

		internal static T Deserialize<T>(byte[] bytes)
		{
			using (var stream = new MemoryStream(bytes)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
		}

		internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellation)
		{
			var bytes = Serialize(value);
			if (bytes.Length > MaximumFrameBytes) throw new InvalidDataException("Response exceeds the protocol limit.");
			var length = BitConverter.GetBytes(bytes.Length);
			await stream.WriteAsync(length, 0, length.Length, cancellation).ConfigureAwait(false);
			await stream.WriteAsync(bytes, 0, bytes.Length, cancellation).ConfigureAwait(false);
			await stream.FlushAsync(cancellation).ConfigureAwait(false);
		}

		internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellation)
		{
			var header = new byte[4];
			await ReadExactlyAsync(stream, header, cancellation).ConfigureAwait(false);
			var length = BitConverter.ToInt32(header, 0);
			if (length < 1 || length > MaximumFrameBytes) throw new InvalidDataException("Invalid protocol frame length.");
			var bytes = new byte[length];
			await ReadExactlyAsync(stream, bytes, cancellation).ConfigureAwait(false);
			return Deserialize<T>(bytes);
		}

		private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellation)
		{
			var offset = 0;
			while (offset < buffer.Length)
			{
				var count = await stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellation).ConfigureAwait(false);
				if (count == 0) throw new EndOfStreamException();
				offset += count;
			}
		}
	}
}
