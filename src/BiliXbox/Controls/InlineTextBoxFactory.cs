using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace BiliXbox.Controls
{
    internal static class InlineTextBoxFactory
    {
        internal static TextBox Replace(TextBox previous, int maxLength)
        {
            if (previous == null) throw new ArgumentNullException(nameof(previous));
            if (maxLength < 1) throw new ArgumentOutOfRangeException(nameof(maxLength));
            var parent = previous.Parent as Panel;
            if (parent == null) throw new InvalidOperationException("输入框必须位于当前页面。");
            int index = parent.Children.IndexOf(previous);
            if (index < 0) throw new InvalidOperationException("输入框尚未显示。");
            if (previous.IsFocusEngaged) previous.IsFocusEngaged = false;
            string stage = "文字";
            try
            {
                var editor = new TextBox
                {
                    Text = previous.Text, MaxLength = maxLength, PlaceholderText = previous.PlaceholderText,
                    AcceptsReturn = previous.AcceptsReturn, TextWrapping = previous.TextWrapping,
                    IsEnabled = previous.IsEnabled, IsReadOnly = false, IsTabStop = true,
                    IsFocusEngagementEnabled = true, IsSpellCheckEnabled = false
                };
                stage = "样式";
                if (previous.Style != null) editor.Style = previous.Style;
                editor.FontSize = previous.FontSize;
                editor.CornerRadius = previous.CornerRadius;
                editor.HorizontalAlignment = previous.HorizontalAlignment;
                editor.VerticalAlignment = previous.VerticalAlignment;
                editor.Margin = previous.Margin; editor.Padding = previous.Padding;
                editor.Visibility = previous.Visibility;
                stage = "输入法";
                // InputScope is a native association object. Give the renewed
                // editor its own scope rather than reparenting the old instance.
                var scope = new InputScope();
                if (previous.InputScope != null)
                    foreach (var name in previous.InputScope.Names)
                        scope.Names.Add(new InputScopeName { NameValue = name.NameValue });
                if (scope.Names.Count > 0) editor.InputScope = scope;
                stage = "布局";
                if (Finite(previous.Width)) editor.Width = previous.Width;
                if (Finite(previous.Height)) editor.Height = previous.Height;
                editor.MinWidth = previous.MinWidth; editor.MinHeight = previous.MinHeight;
                if (Finite(previous.MaxWidth)) editor.MaxWidth = previous.MaxWidth;
                if (Finite(previous.MaxHeight)) editor.MaxHeight = previous.MaxHeight;
                Grid.SetRow(editor, Grid.GetRow(previous)); Grid.SetColumn(editor, Grid.GetColumn(previous));
                Grid.SetRowSpan(editor, Grid.GetRowSpan(previous)); Grid.SetColumnSpan(editor, Grid.GetColumnSpan(previous));
                stage = "标识";
                AutomationProperties.SetName(editor, AutomationProperties.GetName(previous));
                AutomationProperties.SetAutomationId(editor, string.IsNullOrEmpty(AutomationProperties.GetAutomationId(previous))
                    ? previous.Name : AutomationProperties.GetAutomationId(previous));
                // Keep the stable automation ID; do not register a duplicate
                // XAML Name while the original named element is still in scope.
                stage = "替换";
                parent.Children.RemoveAt(index);
                try { parent.Children.Insert(index, editor); }
                catch { parent.Children.Insert(index, previous); throw; }
                return editor;
            }
            catch (Exception error) { throw new InvalidOperationException("无法打开原位输入框（" + stage + "）。", error); }
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
