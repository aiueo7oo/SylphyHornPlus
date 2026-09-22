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
				var key = company + "\n" + product + "\n" + identity.User.Value + "\n" + process.SessionId;
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
