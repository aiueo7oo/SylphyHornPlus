using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SylphyHorn.Properties;
using SylphyHorn.UI;
using Xunit;
using static SylphyHorn.Tests.DesktopRuntimeTestData;

namespace SylphyHorn.Tests
{
	[Collection(PlacementUiCollection.Name)]
	public sealed class AppPlacementViewTests
	{
		[Theory]
		[InlineData("ja", 1)]
		[InlineData("en", 1)]
		[InlineData("ja", 1.5)]
		public async Task InlineListsAndSeparatePickerBindAndRender(string culture, double scale)
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
						using (var fixture = await PlacementUiFixture.Create())
						{
							fixture.Harness.Provider.PublishStable(Batch(1, 2, A, Entry(A, 0, "Development", ""), Entry(B, 1, "Web", ""), Entry(C, 2, "Personal", "")));
							fixture.Harness.Owner.Drain();
							fixture.Model.RefreshDestinationChoices();
							var view = new AppPlacementSettingsView
							{
								DataContext = fixture.Model,
								Width = 545,
								Height = 475,
								FontFamily = new FontFamily("Segoe UI, Meiryo UI")
							};
							Theme(view, culture == "en" ? "Dark" : "Light");
							var header = new Style(typeof(TextBlock));
							header.Setters.Add(new Setter(TextBlock.FontSizeProperty, 18.0));
							header.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Segoe UI Light, Meiryo UI")));
							header.Setters.Add(new Setter(TextBlock.ForegroundProperty, view.FindResource("AccentBrushKey")));
							view.Resources.Add("HeaderStyleKey", header);
							var named = await fixture.Add(@"C:\Applications\Editor\Editor.exe", name: "Development");
							await fixture.Add(@"D:\Portable\Browser\Browser.exe", 2);
							var paused = await fixture.Add(@"C:\Apps\Notes.exe", 3);
							paused.Enabled = false;
							await fixture.Model.CommitAsync(paused);
							Render(view, scale);
							var destinations = Descendants(view).OfType<ComboBox>().ToArray();
							Assert.Equal(3, destinations.Length);
							Assert.All(destinations, combo => Assert.True(combo.IsEditable));
							Assert.Equal(new[] { "1", "2", "3" }, destinations[1].Items.Cast<string>());
							Assert.Equal("Development", destinations[0].Text);
							destinations[0].SelectedItem = "Web";
							Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
							Assert.Equal("Web", named.Destination);
							destinations[0].Text = "Future";
							fixture.Harness.Provider.PublishStable(Batch(1, 3, A, Entry(A, 0, "Renamed", "")));
							fixture.Harness.Owner.Drain();
							fixture.Model.RefreshDestinationChoices();
							Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
							Assert.Equal("Future", named.Destination);
							Assert.Equal("Future", destinations[0].Text);
							var inputs = Descendants(view).OfType<TextBox>().ToArray();
							var path = Assert.Single(inputs, input => input.Text == named.AppText);
							Assert.False(path.IsReadOnly);
							Assert.True(path.Focusable && path.IsTabStop);
							Assert.InRange(path.TransformToAncestor(view).TransformBounds(new Rect(path.RenderSize)).Right, 1, view.Width);
							Assert.Equal(2, Descendants(view).OfType<Button>().Count(button => Equals(button.Content, fixture.Model.Text["AddManual"])));
							Assert.Empty(Descendants(view).OfType<RadioButton>());
							using (var picker = fixture.Model.CreatePicker())
							{
								fixture.Catalog.Pending = Task.FromResult<IReadOnlyList<SylphyHorn.Services.AppPlacement.PlacementAppChoice>>(new[]
								{
									PlacementUiCatalog.Choice(@"C:\Programs\Editor.exe"),
									PlacementUiCatalog.Choice(@"D:\Portable\Editor.exe")
								});
								await picker.WindowsCommand.ExecuteAsync(null);
								picker.Selected = picker.Candidates[1];
								var selection = new PlacementAppPickerView
								{
									DataContext = picker,
									Width = 640,
									Height = 460,
									FontFamily = view.FontFamily
								};
								Theme(selection, culture == "en" ? "Dark" : "Light");
								Render(selection, scale);
								var detail = Assert.Single(Descendants(selection).OfType<TextBox>(), input => input.Text == picker.Details);
								Assert.True(detail.IsReadOnly && detail.Focusable);
								Assert.Equal(ScrollBarVisibility.Auto, detail.HorizontalScrollBarVisibility);
								foreach (var button in Descendants(selection).OfType<Button>().Where(button => button.IsDefault || button.IsCancel))
									Assert.InRange(button.TransformToAncestor(selection).TransformBounds(new Rect(button.RenderSize)).Bottom, 1, selection.Height);
							}
							using (var applyFixture = await PlacementApplyFixture.Create())
							{
								await applyFixture.Model.RefreshCommand.ExecuteAsync(null);
								applyFixture.Model.Rows[0].Selected = true;
								var applyView = new PlacementApplyView
								{
									DataContext = applyFixture.Model,
									Width = 870,
									Height = 470,
									FontFamily = view.FontFamily
								};
								Theme(applyView, culture == "en" ? "Dark" : "Light");
								Render(applyView, scale);
								var move = Assert.Single(Descendants(applyView).OfType<Button>(), button => ReferenceEquals(button.Command, applyFixture.Model.ApplyCommand));
								Assert.True(move.IsEnabled);
								Assert.False(move.IsDefault);
								Assert.InRange(move.TransformToAncestor(applyView).TransformBounds(new Rect(move.RenderSize)).Bottom, 1, applyView.Height);
								Assert.Equal(3, Descendants(applyView).OfType<CheckBox>().Count());
								((ListBox)applyView.FindName("Windows")).SelectedIndex = 0;
								Render(applyView, scale);
								Assert.Contains(Descendants(applyView).OfType<TextBox>(), input => input.IsReadOnly && input.Text == applyFixture.Model.Rows[0].Path);
								await applyFixture.Model.ApplyCommand.ExecuteAsync(null);
								Render(applyView, scale);
								Assert.False(move.IsEnabled);
								var title = Assert.Single(Descendants(applyView).OfType<TextBlock>(), text => text.Text == applyFixture.Model.Rows[0].Title);
								Assert.True(title.ActualWidth > 100, "Window title width: " + title.ActualWidth);
							}
							var edited = fixture.Model.Groups[1].Rows[0];
							edited.Destination = "invalid";
							await fixture.Model.CommitAsync(edited);
							var originalSetting = SylphyHorn.Serialization.Settings.General.Culture.Value;
							try
							{
								foreach (var language in new[] { "en", "ja" })
								{
									SylphyHorn.Services.ResourceService.Current.ChangeCulture(language);
									Render(view, scale);
									Assert.Contains(Descendants(view).OfType<TextBlock>(), text => text.Text == fixture.Model.Text["NameList"]);
									Assert.Contains(Descendants(view).OfType<CheckBox>(), box => Equals(box.Content, fixture.Model.Text["Enable"]));
									Assert.Contains(Descendants(view).OfType<Button>(), button => Equals(button.Content, language == "en" ? "Delete" : "削除"));
									foreach (var button in Descendants(view).OfType<Button>().Where(button => Equals(button.Content, fixture.Model.Text["DeleteLabel"])))
									{
										var width = button.ActualWidth;
										button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
										var required = button.DesiredSize.Width - button.Margin.Left - button.Margin.Right;
										Assert.True(width >= required - 0.5, $"Delete button width {width} is smaller than its content width {required}.");
									}
									Assert.Contains(Descendants(view).OfType<TextBlock>(), text => text.Text == fixture.Model.Text["InvalidNumber"]);
									Assert.Same(edited, fixture.Model.Groups[1].Rows[0]);
									Assert.Equal("invalid", edited.Destination);
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

		private static void Theme(Control view, string theme)
		{
			foreach (var resource in new[]
			{
				"MetroRadiance;component/Styles/Controls.xaml",
				"MetroRadiance;component/Styles/Icons.xaml",
				"MetroRadiance;component/Themes/" + theme + ".xaml",
				"MetroRadiance;component/Themes/Accents/Blue.xaml",
				"MetroTrilithon.Desktop;component/Styles/Controls.xaml",
				"SylphyHorn;component/Styles/Controls.xaml"
			})
				view.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/" + resource) });
			view.SetResourceReference(Control.BackgroundProperty, "ThemeBrushKey");
			view.SetResourceReference(Control.ForegroundProperty, "ActiveForegroundBrushKey");
		}

		private static void Render(FrameworkElement view, double scale)
		{
			view.Measure(new Size(view.Width, view.Height));
			view.Arrange(new Rect(0, 0, view.Width, view.Height));
			view.UpdateLayout();
			Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
			// Off-screen visuals need repainting for each bitmap, including unchanged, trimmed text.
			foreach (var element in Descendants(view).OfType<UIElement>()) element.InvalidateVisual();
			view.UpdateLayout();
			var bitmap = new RenderTargetBitmap(
				(int)(view.Width * scale), (int)(view.Height * scale),
				96 * scale, 96 * scale, PixelFormats.Pbgra32);
			bitmap.Render(view);
		}

		private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
		{
			for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
			{
				var child = VisualTreeHelper.GetChild(root, i);
				yield return child;
				foreach (var nested in Descendants(child)) yield return nested;
			}
		}

		private sealed class BindingErrors : TraceListener
		{
			internal readonly List<string> Messages = new List<string>();

			public override void Write(string message)
			{
				if (!string.IsNullOrEmpty(message)) this.Messages.Add(message);
			}

			public override void WriteLine(string message) => this.Write(message);
		}
	}
}
