using System;
using System.Collections;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SylphyHorn.UI
{
	// Keyboard, focus and drop-down handling shared by the inline entry rows of the settings pages
	// (app placement, desktop auto-close and creation wallpapers).
	internal static class EditableRowInput
	{
		// The text a destination combo box showed when its list was opened, restored by Esc.
		// null while the list is closed, and after Esc so that closing the list does not commit.
		private static readonly DependencyProperty TextBeforeListOpenedProperty = DependencyProperty.RegisterAttached(
			"TextBeforeListOpened",
			typeof(string),
			typeof(EditableRowInput));

		// Whether the IME is on while typing in an editable combo box; false for desktop numbers.
		// InputMethod.IsInputMethodEnabled is read from the focused text box inside the template and is not inherited
		// from the combo box, so the value is copied to that text box.
		public static readonly DependencyProperty UsesInputMethodProperty = DependencyProperty.RegisterAttached(
			"UsesInputMethod",
			typeof(bool),
			typeof(EditableRowInput),
			new PropertyMetadata(true, OnUsesInputMethodChanged));

		public static bool GetUsesInputMethod(DependencyObject element) => (bool)element.GetValue(UsesInputMethodProperty);

		public static void SetUsesInputMethod(DependencyObject element, bool value) => element.SetValue(UsesInputMethodProperty, value);

		private static void OnUsesInputMethodChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
		{
			var combo = (ComboBox)element;
			if (!TryCopyInputMethodToEditor(combo))
			{
				// A combo box created from a data template gets its template during the first layout.
				combo.Dispatcher.BeginInvoke(new Action(() => TryCopyInputMethodToEditor(combo)), DispatcherPriority.Loaded);
			}
			// Copied again before focus enters, in case the template has been replaced since.
			combo.PreviewGotKeyboardFocus -= CopyInputMethodBeforeFocus;
			combo.PreviewGotKeyboardFocus += CopyInputMethodBeforeFocus;
		}

		private static void CopyInputMethodBeforeFocus(object sender, KeyboardFocusChangedEventArgs args)
			=> TryCopyInputMethodToEditor((ComboBox)sender);

		private static bool TryCopyInputMethodToEditor(ComboBox combo)
		{
			if (!(combo.Template?.FindName("PART_EditableTextBox", combo) is TextBox editor)) return false;
			InputMethod.SetIsInputMethodEnabled(editor, GetUsesInputMethod(combo));
			return true;
		}

		// Shows the current destination choices in this control only, preserving text typed in other rows
		// even when a desktop was renamed or removed.
		internal static void DestinationListOpened(ComboBox combo, IEnumerable choices)
		{
			var text = combo.Text;
			combo.ItemsSource = choices;
			combo.Text = text;
			combo.SetValue(TextBeforeListOpenedProperty, text);
		}

		// Returns whether the closed list should be committed: false when Esc already restored the text.
		internal static bool DestinationListClosed(ComboBox combo)
		{
			if (combo.GetValue(TextBeforeListOpenedProperty) == null) return false;
			combo.ClearValue(TextBeforeListOpenedProperty);
			return true;
		}

		internal static void CloseListWithoutCommit(ComboBox combo)
		{
			combo.ClearValue(TextBeforeListOpenedProperty);
			combo.IsDropDownOpen = false;
		}

		private static void RestoreTextBeforeListOpened(ComboBox combo)
		{
			var text = (string)combo.GetValue(TextBeforeListOpenedProperty);
			CloseListWithoutCommit(combo);
			combo.Text = text;
		}

		// A combo box commits when its list closes or focus leaves it, not while focus moves into its own list or text.
		internal static bool IsFocusMovingWithinComboBox(object sender, EventArgs args)
		{
			return sender is ComboBox combo
				&& args is KeyboardFocusChangedEventArgs
				&& (combo.IsDropDownOpen || combo.IsKeyboardFocusWithin);
		}

		// Handles Esc and Enter typed in a text field or combo box of an entry. Esc while an editable list is open
		// restores the text shown before it opened; otherwise Esc reverts the entry and Enter commits it.
		// Handled on the entry, before ComboBox's class handler closes the popup and commits it.
		internal static async Task HandleEntryKeyAsync(DependencyObject entry, KeyEventArgs args, Action revert, Func<Task> commit)
		{
			var source = args.OriginalSource as DependencyObject;
			var combo = FindAncestorWithin<ComboBox>(source, entry);
			if (combo == null && FindAncestorWithin<TextBox>(source, entry) == null) return;
			if (combo != null && combo.IsDropDownOpen)
			{
				// A list-only combo box (the switch setting) closes its own list; only typed destinations are restored here.
				if (args.Key == Key.Escape && combo.IsEditable)
				{
					RestoreTextBeforeListOpened(combo);
					args.Handled = true;
				}
				return;
			}
			if (args.Key == Key.Escape)
			{
				revert();
				args.Handled = true;
			}
			else if (args.Key == Key.Enter)
			{
				args.Handled = true;
				await commit();
			}
		}

		private static T FindAncestorWithin<T>(DependencyObject source, DependencyObject boundary) where T : DependencyObject
		{
			for (var current = source; current != null && current != boundary; current = VisualTreeHelper.GetParent(current))
			{
				if (current is T found)
				{
					return found;
				}
			}
			return null;
		}

		// Moves focus to the entry field bound to the given property of the row, selecting its text when asked.
		internal static void FocusField(FrameworkElement view, object row, string property, bool selectText)
		{
			view.UpdateLayout();
			var field = FindField(view, row, property);
			field?.BringIntoView();
			field?.Focus();
			if (!selectText) return;
			if (field is TextBox text)
			{
				text.SelectAll();
			}
			else if (field is ComboBox combo && combo.Template.FindName("PART_EditableTextBox", combo) is TextBox editor)
			{
				editor.Focus();
				editor.SelectAll();
			}
		}

		internal static Control FindField(DependencyObject parent, object row, string property)
		{
			for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
			{
				var child = VisualTreeHelper.GetChild(parent, index);
				if (child is TextBox text && ReferenceEquals(text.DataContext, row) && IsBoundTo(text, TextBox.TextProperty, property))
				{
					return text;
				}
				if (child is ComboBox combo && ReferenceEquals(combo.DataContext, row) && IsBoundTo(combo, ComboBox.TextProperty, property))
				{
					return combo;
				}
				var found = FindField(child, row, property);
				if (found != null) return found;
			}
			return null;
		}

		private static bool IsBoundTo(FrameworkElement element, DependencyProperty target, string property)
			=> element.GetBindingExpression(target)?.ParentBinding.Path.Path == property;
	}
}
