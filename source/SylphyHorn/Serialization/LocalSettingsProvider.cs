using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Xml;
using SylphyHorn.AppPlacement;
using SylphyHorn.Properties;

namespace SylphyHorn.Serialization
{
	public sealed class LocalSettingsProvider : DictionaryProvider
	{
		public static readonly string SupportedFormats = "XML (*.xml)|*.xml";
		public static TimeSpan FileSystemHandlerThrottleDueTime { get; set; } = TimeSpan.FromMilliseconds(1500);
		public static LocalSettingsProvider Instance { get; } = new LocalSettingsProvider();

		private readonly FileInfo _targetFile;
		public bool Available { get; }
		public string FilePath => this._targetFile?.FullName;

		private LocalSettingsProvider()
		{
			var path = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				ProductInfo.Company,
				ProductInfo.Product,
				this.Filename);
			var file = new FileInfo(path);
			if (file.Directory == null || file.DirectoryName == null)
			{
				this.Available = false;
				return;
			}
			if (!file.Directory.Exists) file.Directory.Create();
			this._targetFile = file;
			this.Available = true;
		}

		public async Task LoadOrMigrateAsync()
		{
			if (this.Available && File.Exists(this._targetFile.FullName))
			{
				await this.LoadAsync().ConfigureAwait(false);
				return;
			}

			var path = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				ProductInfo.OriginalCompany,
				ProductInfo.OriginalProduct,
				this.Filename);
			if (File.Exists(path)) await this.ImportAsync(path).ConfigureAwait(false);
			else await this.LoadAsync().ConfigureAwait(false);
		}

		protected override Task SaveAsyncCore(IDictionary<string, object> dic)
		{
			if (!this.Available) throw new InvalidOperationException("The local settings provider is unavailable.");
			return AtomicSettingsFile.WriteAsync(dic, this._targetFile, this.KnownTypes);
		}

		protected override Task SaveAsyncCore(IDictionary<string, object> dic, string path)
		{
			if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A target path is required.", nameof(path));
			return AtomicSettingsFile.WriteAsync(dic, new FileInfo(path), this.KnownTypes);
		}

		protected override Task<IDictionary<string, object>> LoadAsyncCore()
		{
			if (!this.Available) return Task.FromResult<IDictionary<string, object>>(null);
			return AtomicSettingsFile.ReadAsync(this._targetFile, this.KnownTypes);
		}

		protected override Task<IDictionary<string, object>> LoadAsyncCore(string path)
		{
			if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A source path is required.", nameof(path));
			return AtomicSettingsFile.ReadAsync(new FileInfo(path), this.KnownTypes);
		}

		protected override Task<string> GetContentHashAsyncCore()
			=> !this.Available ? Task.FromResult<string>(null) : AtomicSettingsFile.HashAsync(this._targetFile);
	}

	internal static class AtomicSettingsFile
	{
		// Older versions can preserve string values without knowing the feature's CLR types.
		private static readonly Dictionary<string, Type> StructuredTypes = new Dictionary<string, Type>(StringComparer.Ordinal)
		{
			[AppPlacementSettings.ConfigurationKey] = typeof(AppPlacementConfiguration),
			[AppPlacementSettings.CreatedDesktopGroupsKey] = typeof(PlacementCreatedGroup[]),
			[DesktopWallpaperOnCreation.SettingsKey] = typeof(DesktopWallpaperOnCreation[]),
		};

		private static IDictionary<string, object> EncodeValues(IDictionary<string, object> values)
		{
			var stored = new Dictionary<string, object>(values, StringComparer.Ordinal);
			foreach (var entry in StructuredTypes)
			{
				if (!values.TryGetValue(entry.Key, out var value)) continue;
				if (value == null || value.GetType() != entry.Value)
				{
					throw new SerializationException("Invalid settings value: " + entry.Key);
				}
				using (var output = new StringWriter())
				{
					using (var writer = XmlWriter.Create(output, new XmlWriterSettings { OmitXmlDeclaration = true }))
						new DataContractSerializer(entry.Value).WriteObject(writer, value);
					stored[entry.Key] = output.ToString();
				}
			}
			return stored;
		}

		internal static Task WriteAsync(IDictionary<string, object> dictionary, FileInfo targetFile, Type[] knownTypes)
		{
			if (dictionary == null) throw new ArgumentNullException(nameof(dictionary));
			if (targetFile?.Directory == null || targetFile.DirectoryName == null) throw new InvalidOperationException("The settings target directory is unavailable.");
			return Task.Run(() =>
			{
				if (!targetFile.Directory.Exists) targetFile.Directory.Create();
				var tempPath = Path.Combine(targetFile.DirectoryName, targetFile.Name + "." + Guid.NewGuid().ToString("N") + ".tmp");
				try
				{
					var stored = EncodeValues(dictionary);
					var serializer = new DataContractSerializer(typeof(IDictionary<string, object>), knownTypes);
					var writerSettings = new XmlWriterSettings { Indent = true, CloseOutput = false };
					using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
					{
						using (var writer = XmlWriter.Create(stream, writerSettings))
						{
							serializer.WriteObject(writer, stored);
							writer.Flush();
						}
						stream.Flush(true);
					}
					if (File.Exists(targetFile.FullName)) File.Replace(tempPath, targetFile.FullName, null, true);
					else File.Move(tempPath, targetFile.FullName);
				}
				finally
				{
					if (File.Exists(tempPath)) File.Delete(tempPath);
				}
			});
		}

		internal static Task<IDictionary<string, object>> ReadAsync(FileInfo file, Type[] knownTypes)
		{
			if (file == null || !File.Exists(file.FullName)) return Task.FromResult<IDictionary<string, object>>(null);
			return Task.Run(() =>
			{
				if (!File.Exists(file.FullName)) return null;
				var serializer = new DataContractSerializer(typeof(IDictionary<string, object>), knownTypes);
				using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
				{
					var values = serializer.ReadObject(stream) as IDictionary<string, object>;
					if (values == null) return null;
					foreach (var entry in StructuredTypes)
					{
						if (values.TryGetValue(entry.Key, out var value) && value is string xml)
						{
							using (var input = new StringReader(xml))
							using (var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
							{
								values[entry.Key] = new DataContractSerializer(entry.Value).ReadObject(reader);
								if (reader.MoveToContent() != XmlNodeType.None)
								{
									throw new SerializationException("Unexpected content after a settings value.");
								}
							}
						}
					}
					return values;
				}
			});
		}

		internal static Task<string> HashAsync(FileInfo file)
		{
			if (file == null || !File.Exists(file.FullName)) return Task.FromResult<string>(null);
			return Task.Run(() =>
			{
				using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
				using (var sha = SHA256.Create())
					return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
			});
		}
	}
}
