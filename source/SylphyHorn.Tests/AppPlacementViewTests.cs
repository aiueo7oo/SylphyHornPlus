using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
							// About the page width inside the 760-wide settings window, after the tab list.
							var view = new AppPlacementSettingsView
							{
								DataContext = fixture.Model,
								Width = 640,
								Height = 475,
								FontFamily = new FontFamily("Segoe UI, Meiryo UI")
							};
							Theme(view, culture == "en" ? "Dark" : "Light");
							AddHeaderStyle(view);
							var closeView = new DesktopAutoCloseSettingsView
							{
								DataContext = fixture.Model,
								Width = 640,
								Height = 475,
								FontFamily = view.FontFamily
							};
							Theme(closeView, culture == "en" ? "Dark" : "Light");
							AddHeaderStyle(closeView);
							var named = await fixture.Add(@"C:\Applications\Editor\Editor.exe", name: "Development");
							await fixture.Add(@"D:\Portable\Browser\Browser.exe", 2);
							var paused = await fixture.Add(@"C:\Apps\Notes.exe", 3);
							paused.Enabled = false;
							await fixture.Model.CommitAsync(paused);
							Render(view, scale);
							AssertEntryLayout(view, fixture.Model);
							var destinations = Descendants(view).OfType<ComboBox>().Where(combo => combo.IsEditable).ToArray();
							Assert.Equal(3, destinations.Length);
							Assert.Equal(new[] { "1", "2", "3" }, destinations[1].Items.Cast<string>());
							Assert.Equal("Development", destinations[0].Text);
							Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
							// Name destinations keep the IME; number destinations turn it off, including the editor inside the combo box.
							Assert.True(InputMethod.GetIsInputMethodEnabled(EditorOf(destinations[0])));
							Assert.All(destinations.Skip(1), combo => Assert.False(InputMethod.GetIsInputMethodEnabled(EditorOf(combo))));
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
							Assert.DoesNotContain(Descendants(view).OfType<CheckBox>(), box => Equals(box.Content, fixture.Model.Text["CloseCreated"]));
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
								{
									Assert.InRange(button.TransformToAncestor(selection).TransformBounds(new Rect(button.RenderSize)).Bottom, 1, selection.Height);
								}
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
							var edited = fixture.Model.NumberGroup.Rows[0];
							edited.Destination = "invalid";
							await fixture.Model.CommitAsync(edited);
							var closing = fixture.Model.AddClosingRow(fixture.Model.NumberClosingGroup);
							closing.Destination = "3";
							await fixture.Model.CommitClosingAsync(closing);
							var originalSetting = SylphyHorn.Serialization.Settings.General.Culture.Value;
							try
							{
								foreach (var language in new[] { "en", "ja" })
								{
									SylphyHorn.Services.ResourceService.Current.ChangeCulture(language);
									Render(view, scale);
									Render(closeView, scale);
									AssertEntryLayout(view, fixture.Model);
									Assert.Contains(Descendants(view).OfType<TextBlock>(), text => text.Text == fixture.Model.Text["NameList"]);
									Assert.Contains(Descendants(closeView).OfType<CheckBox>(), box => Equals(box.Content, fixture.Model.Text["CloseCreated"]));
									Assert.Contains(Descendants(closeView).OfType<ComboBox>(), combo => ReferenceEquals(combo.DataContext, closing) && combo.Text == "3");
									var closingEditor = EditorOf(Descendants(closeView).OfType<ComboBox>().Single(combo => ReferenceEquals(combo.DataContext, closing)));
									Assert.False(InputMethod.GetIsInputMethodEnabled(closingEditor));
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
									Assert.Same(edited, fixture.Model.NumberGroup.Rows[0]);
									Assert.Equal("invalid", edited.Destination);
								}
							}
							finally
							{
								SylphyHorn.Services.ResourceService.Current.ChangeCulture(originalSetting);
								Resources.Culture = CultureInfo.GetCultureInfo(culture);
							}
							fixture.Model.CloseCreatedDesktops = true;
							foreach (var enabled in new[] { false, true })
							{
								fixture.Model.IsEnabled = enabled;
								foreach (var createMissing in new[] { false, true })
								{
									fixture.Model.CreateMissingDesktops = createMissing;
									Render(closeView, scale);
									var closeCreated = Assert.Single(Descendants(closeView).OfType<CheckBox>());
									Assert.Equal(enabled && createMissing, closeCreated.IsEnabled);
									Assert.True(closeCreated.IsChecked);
									Assert.True(fixture.Settings.Configuration.Value.CloseCreatedDesktops);
								}
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

		internal static void AddHeaderStyle(FrameworkElement view)
		{
			var header = new Style(typeof(TextBlock));
			header.Setters.Add(new Setter(TextBlock.FontSizeProperty, 18.0));
			header.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Segoe UI Light, Meiryo UI")));
			header.Setters.Add(new Setter(TextBlock.ForegroundProperty, view.FindResource("AccentBrushKey")));
			header.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 8, 0, 8)));
			view.Resources.Add("HeaderStyleKey", header);
		}

		// The two-line entry: switch combo boxes share the longest label's width and are not clipped,
		// Delete and "…" share one right edge, and the path text starts after the icon.
		private static void AssertEntryLayout(FrameworkElement view, SylphyHorn.UI.Bindings.AppPlacementSettingsViewModel model)
		{
			Rect Bounds(FrameworkElement element) => element.TransformToAncestor(view).TransformBounds(new Rect(element.RenderSize));
			var switches = Descendants(view).OfType<ComboBox>().Where(combo => !combo.IsEditable).ToArray();
			Assert.Equal(1 + model.FollowWidthSamples.Count + model.Groups.Sum(group => group.Rows.Count), switches.Length);
			// The visible ones (global and per rule) share one width; the hidden probes carry the longest labels.
			var visible = switches.Where(combo => !(combo.DataContext is SylphyHorn.UI.Bindings.PlacementFollowOption)).ToArray();
			Assert.All(visible, combo => Assert.InRange(combo.ActualWidth, visible[0].ActualWidth - 0.5, visible[0].ActualWidth + 0.5));
			foreach (var probe in switches.Except(visible))
			{
				probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
				var required = probe.DesiredSize.Width - probe.Margin.Left - probe.Margin.Right;
				Assert.True(visible[0].ActualWidth >= required - 0.01, $"Switch width {visible[0].ActualWidth} is smaller than {required}.");
			}
			foreach (var row in model.Groups.SelectMany(group => group.Rows))
			{
				var controls = Descendants(view).OfType<FrameworkElement>().Where(element => ReferenceEquals(element.DataContext, row)).ToArray();
				var follow = Assert.Single(controls.OfType<ComboBox>(), combo => !combo.IsEditable);
				var delete = Assert.Single(controls.OfType<Button>(), button => ReferenceEquals(button.Command, row.RemoveCommand));
				var browse = Assert.Single(controls.OfType<Button>(), button => Equals(button.Content, "…"));
				Assert.True(Bounds(follow).Right <= Bounds(delete).Left + 0.5, $"Switch combo box overlaps Delete: {Bounds(follow)} / {Bounds(delete)}.");
				Assert.InRange(Bounds(delete).Right, Bounds(browse).Right - 0.5, Bounds(browse).Right + 0.5);
				Assert.InRange(Bounds(delete).Right, 1, view.Width);
				var path = Assert.Single(controls.OfType<TextBox>(), input => input.Text == row.AppText);
				var text = Descendants(path).OfType<ScrollContentPresenter>().First();
				// Check box marks and drop-down arrows are paths too; the empty-state glyph is the 16x16 one.
				var glyph = Assert.Single(controls.OfType<System.Windows.Shapes.Path>(), shape => shape.Width == 16);
				Assert.Equal(row.Icon == null ? Visibility.Visible : Visibility.Collapsed, glyph.Visibility);
				Assert.True(Bounds(text).Left >= Bounds(glyph).Right + 2, $"Path text starts at {Bounds(text).Left}, icon ends at {Bounds(glyph).Right}.");
			}
		}

		internal static void Theme(Control view, string theme)
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
			{
				view.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/" + resource) });
			}
			view.SetResourceReference(Control.BackgroundProperty, "ThemeBrushKey");
			view.SetResourceReference(Control.ForegroundProperty, "ActiveForegroundBrushKey");
		}

		internal static void Render(FrameworkElement view, double scale)
		{
			view.Measure(new Size(view.Width, view.Height));
			view.Arrange(new Rect(0, 0, view.Width, view.Height));
			view.UpdateLayout();
			Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
			// Off-screen visuals need repainting for each bitmap, including unchanged, trimmed text.
			foreach (var element in Descendants(view).OfType<UIElement>())
			{
				element.InvalidateVisual();
			}
			view.UpdateLayout();
			var bitmap = new RenderTargetBitmap(
				(int)(view.Width * scale), (int)(view.Height * scale),
				96 * scale, 96 * scale, PixelFormats.Pbgra32);
			bitmap.Render(view);
		}

		internal static TextBox EditorOf(ComboBox combo) => (TextBox)combo.Template.FindName("PART_EditableTextBox", combo);

		internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
		{
			for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
			{
				var child = VisualTreeHelper.GetChild(root, i);
				yield return child;
				foreach (var nested in Descendants(child))
				{
					yield return nested;
				}
			}
		}

		internal sealed class BindingErrors : TraceListener
		{
			internal readonly List<string> Messages = new List<string>();

			public override void Write(string message)
			{
				if (!string.IsNullOrEmpty(message))
				{
					this.Messages.Add(message);
				}
			}

			public override void WriteLine(string message) => this.Write(message);
		}
	}
}
