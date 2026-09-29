using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SylphyHorn.Properties;
using SylphyHorn.Serialization;
using SylphyHorn.UI;
using SylphyHorn.UI.Bindings;
using Xunit;
using static SylphyHorn.Tests.AppPlacementViewTests;
using static SylphyHorn.Tests.DesktopRuntimeTestData;

namespace SylphyHorn.Tests
{
	[Collection(PlacementUiCollection.Name)]
	public sealed class CreationWallpaperViewTests
	{
		[Theory]
		[InlineData("ja", "Light", 1)]
		[InlineData("en", "Dark", 1)]
		[InlineData("ja", "Dark", 1.5)]
		[InlineData("en", "Light", 1.5)]
		public async Task OneLineEntriesBindAndFitThePage(string culture, string theme, double scale)
		{
			var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var thread = new Thread(() =>
			{
				var dispatcher = Dispatcher.CurrentDispatcher;
				SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
				dispatcher.BeginInvoke(new Action(async () =>
				{
					var previous = Resources.Culture;
					var errors = new BindingErrors();
					PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
					try
					{
						Resources.Culture = CultureInfo.GetCultureInfo(culture);
						var missing = @"D:\Images\presentation.png";
						using (var f = await CreationWallpaperUiFixture.Create(new[]
						{
							new DesktopWallpaperOnCreation("Presentation", null, missing),
							new DesktopWallpaperOnCreation("Development", null, @"C:\Users\Public\Pictures\Wallpapers\Development\2026\aurora-borealis-over-the-northern-lake-3840x2160.jpg"),
							new DesktopWallpaperOnCreation(null, 3, @"C:\Wallpapers\third.jpg")
						}, missing: new[] { missing }))
						{
							f.Harness.Provider.PublishStable(Batch(1, 2, A, Entry(A, 0, "Development", ""), Entry(B, 1, "Web", "")));
							f.Harness.Owner.Drain();
							f.Model.RefreshDestinationChoices();
							var draft = f.Model.AddRow(f.Model.NumberGroup);
							var view = new CreationWallpaperSettingsView
							{
								DataContext = f.Model,
								Width = 640,
								Height = 475,
								FontFamily = new FontFamily("Segoe UI, Meiryo UI")
							};
							Theme(view, theme);
							AddHeaderStyle(view);
							await Until(() => f.Model.NameGroup.Rows[0].Error.Length > 0);
							var saved = f.Settings.DesktopWallpapersOnCreation.Value;
							Render(view, scale);
							AssertLayout(view, f.Model);
							var longRow = f.Model.NameGroup.Rows[1];
							var longBox = PathBox(view, longRow);
							Assert.Equal(longRow.Saved.WallpaperPath, longBox.Text);
							Assert.Equal(longRow.WallpaperPath, longBox.ToolTip);
							// Rendering, layout and leaving a field without changes neither edit nor save anything.
							longBox.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(
								System.Windows.Input.Keyboard.PrimaryDevice, 0, longBox, null)
							{
								RoutedEvent = System.Windows.Input.Keyboard.LostKeyboardFocusEvent,
							});
							Render(view, scale);
							Assert.Same(saved, f.Settings.DesktopWallpapersOnCreation.Value);
							Assert.False(longRow.IsEdited);
							Assert.Equal("", longRow.Error);
							Assert.Contains(Descendants(view).OfType<TextBlock>(), text => Shown(text) && text.Text == f.Model.Text["MissingImage"]);
							Assert.DoesNotContain(Descendants(view).OfType<TextBlock>(), text => Shown(text) && text.Text.Contains(Resources.Settings_Background_ChangeBackground));
							var destinations = Descendants(view).OfType<ComboBox>().ToArray();
							Assert.Equal(4, destinations.Length);
							Assert.Equal(new[] { "Development", "Web" }, destinations[0].Items.Cast<string>());
							Assert.Equal(new[] { "1", "2" }, destinations[2].Items.Cast<string>());
							Assert.Equal("", Assert.Single(destinations, combo => ReferenceEquals(combo.DataContext, draft)).Text);

							// A save failure and a discarded edit are shown together; the retry button stays with the save failure.
							f.Harness.Settings.Provider.SaveFailure = new System.IO.IOException("synthetic");
							await f.Add(false, "5", @"C:\Wallpapers\fifth.jpg");
							longRow.WallpaperPath = @"C:\Wallpapers\typing";
							f.Settings.DesktopWallpapersOnCreation.Value = f.Settings.DesktopWallpapersOnCreation.Value.Where(item => item.Name != "Development").ToArray();
							Render(view, scale);
							var saveMessage = Assert.Single(Descendants(view).OfType<TextBlock>(), text => text.Text == f.Model.Text["SaveFailed"]);
							var discarded = Assert.Single(Descendants(view).OfType<TextBlock>(), text => text.Text == f.Model.Text["ConfigurationChanged"]);
							var retry = Assert.Single(Descendants(view).OfType<Button>(), button => ReferenceEquals(button.Command, f.Model.RetrySaveCommand));
							Assert.True(Shown(saveMessage) && Shown(discarded) && Shown(retry));
							Assert.Same(VisualTreeHelper.GetParent(saveMessage), VisualTreeHelper.GetParent(retry));
							f.Harness.Settings.Provider.SaveFailure = null;
							await f.Model.RetrySaveCommand.ExecuteAsync(null);
							Render(view, scale);
							Assert.False(Shown(saveMessage) || Shown(retry));
							Assert.True(Shown(discarded));

							// An empty list shows neither its column captions nor its top rule.
							foreach (var row in f.Model.NameGroup.Rows.ToArray())
							{
								await f.Model.RemoveAsync(row);
							}
							Render(view, scale);
							Assert.DoesNotContain(Descendants(view).OfType<TextBlock>(), text => Shown(text) && text.Text == f.Model.Text["NameColumn"]);
							Assert.Contains(Descendants(view).OfType<TextBlock>(), text => Shown(text) && text.Text == f.Model.Text["NumberColumn"]);

							var originalSetting = Settings.General.Culture.Value;
							try
							{
								foreach (var language in new[] { "en", "ja" })
								{
									SylphyHorn.Services.ResourceService.Current.ChangeCulture(language);
									Render(view, scale);
									AssertLayout(view, f.Model);
									Assert.Contains(Descendants(view).OfType<TextBlock>(), text => text.Text == f.Model.Text["Title"]);
									Assert.Contains(Descendants(view).OfType<Button>(), button => Equals(button.Content, language == "en" ? "Delete" : "削除"));
								}
							}
							finally
							{
								SylphyHorn.Services.ResourceService.Current.ChangeCulture(originalSetting);
								Resources.Culture = CultureInfo.GetCultureInfo(culture);
							}
							Assert.Empty(errors.Messages);
						}
						completion.TrySetResult(true);
					}
					catch (Exception ex)
					{
						completion.TrySetException(ex);
					}
					finally
					{
						Resources.Culture = previous;
						PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
						dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
					}
				}));
				Dispatcher.Run();
			})
			{ IsBackground = true };
			thread.SetApartmentState(ApartmentState.STA);
			thread.Start();
			Assert.Same(completion.Task, await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)));
			await completion.Task;
		}

		private static void AssertLayout(FrameworkElement view, CreationWallpaperSettingsViewModel model)
		{
			Rect Bounds(FrameworkElement element) => element.TransformToAncestor(view).TransformBounds(new Rect(element.RenderSize));
			var scroll = Descendants(view).OfType<ScrollViewer>().First();
			Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 0.5, $"Horizontal overflow: {scroll.ExtentWidth} > {scroll.ViewportWidth}.");
			var page = Bounds(Descendants(scroll).OfType<ScrollContentPresenter>().First());
			double? edge = null;
			foreach (var group in model.Groups)
			{
				var caption = Descendants(view).OfType<TextBlock>().FirstOrDefault(text => Shown(text) && text.Text == group.DestinationLabel);
				Assert.Equal(group.Rows.Count > 0, caption != null);
				foreach (var row in group.Rows)
				{
					var controls = Descendants(view).OfType<FrameworkElement>().Where(element => ReferenceEquals(element.DataContext, row)).ToArray();
					var destination = Assert.Single(controls.OfType<ComboBox>());
					var path = Assert.Single(controls.OfType<TextBox>(), input => !(input.TemplatedParent is ComboBox));
					var browse = Assert.Single(controls.OfType<Button>(), button => Equals(button.Content, "…"));
					var delete = Assert.Single(controls.OfType<Button>(), button => ReferenceEquals(button.Command, row.RemoveCommand));
					Assert.InRange(Bounds(destination).Width, 109.5, 110.5);
					Assert.InRange(Bounds(destination).Left, Bounds(caption).Left - 0.5, Bounds(caption).Left + 0.5);
					Assert.True(Bounds(destination).Right < Bounds(path).Left, "The destination overlaps the path.");
					Assert.True(Bounds(path).Right <= Bounds(browse).Left + 0.5, $"The path overlaps \"…\": {Bounds(path)} / {Bounds(browse)}.");
					Assert.True(Bounds(browse).Right <= Bounds(delete).Left + 0.5, "\"…\" overlaps Delete.");
					Assert.True(Bounds(delete).Right <= page.Right + 0.5, $"Delete ends at {Bounds(delete).Right}, the page at {page.Right}.");
					edge = edge ?? Bounds(delete).Right;
					Assert.InRange(Bounds(delete).Right, edge.Value - 0.5, edge.Value + 0.5);
					var width = delete.ActualWidth;
					delete.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
					Assert.True(width >= delete.DesiredSize.Width - delete.Margin.Left - delete.Margin.Right - 0.5, "Delete is narrower than its label.");
				}
			}
		}

		private static TextBox PathBox(FrameworkElement view, CreationWallpaperRow row)
			=> Descendants(view).OfType<TextBox>().Single(input => ReferenceEquals(input.DataContext, row) && !(input.TemplatedParent is ComboBox));

		// Off-screen test views have no presentation source, so IsVisible is always false; check the Visibility chain instead.
		private static bool Shown(DependencyObject element)
		{
			for (; element != null; element = VisualTreeHelper.GetParent(element))
			{
				if (element is UIElement visual && visual.Visibility != Visibility.Visible) return false;
			}
			return true;
		}

		private static async Task Until(Func<bool> condition)
		{
			for (var waited = 0; waited < 5000 && !condition(); waited += 10)
			{
				await Task.Delay(10);
			}
		}
	}
}
