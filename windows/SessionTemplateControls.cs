using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace CodexPace;

internal sealed class SessionTemplateControls
{
    public Button ApplyButton { get; } = new() { Content = "Apply template" };
    public Button SaveButton { get; } = new() { Content = "Save as template…" };
    public event Action<SavedSessionTemplate>? TemplateSelected;
    private readonly Func<IReadOnlyList<SessionTemplate>> currentSessions;

    public SessionTemplateControls(IEnumerable<SavedSessionTemplate> templates,
        Func<IReadOnlyList<SessionTemplate>> currentSessions, Action<SavedSessionTemplate[]> saveTemplates)
    {
        this.currentSessions = currentSessions;
        var saved = templates.ToList();
        var menu = new MenuFlyout();
        foreach (var template in saved) AddMenuItem(template);
        ApplyButton.Flyout = menu;
        ApplyButton.IsEnabled = menu.Items.Count > 0;

        var name = new TextBox { PlaceholderText = "Template name" };
        AutomationProperties.SetName(name, "Template name");
        var error = new TextBlock { Text = "A template with this name already exists.", TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        var save = new Button { Content = "Save template" };
        var cancel = new Button { Content = "Cancel" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        var form = new StackPanel { Spacing = 8, MinWidth = 240 };
        form.Children.Add(name);
        form.Children.Add(error);
        form.Children.Add(actions);
        var flyout = new Flyout { Content = form };
        SaveButton.Flyout = flyout;

        bool NameExists() => saved.Any(template => string.Equals(template.Name, name.Text.Trim(),
            StringComparison.OrdinalIgnoreCase));

        void ValidateName()
        {
            bool duplicate = NameExists();
            error.Visibility = duplicate ? Visibility.Visible : Visibility.Collapsed;
            save.IsEnabled = !duplicate && !string.IsNullOrWhiteSpace(name.Text);
        }

        void SaveTemplate()
        {
            if (NameExists() || SavedSessionTemplate.Create(name.Text, currentSessions()) is not { } template)
                return;
            saved.Add(template);
            saveTemplates(saved.ToArray());
            AddMenuItem(template);
            ApplyButton.IsEnabled = true;
            flyout.Hide();
        }

        flyout.Opened += (_, _) =>
        {
            name.Text = "";
            ValidateName();
            name.Focus(FocusState.Programmatic);
        };
        name.TextChanged += (_, _) => ValidateName();
        name.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter) return;
            args.Handled = true;
            SaveTemplate();
        };
        save.Click += (_, _) => SaveTemplate();
        cancel.Click += (_, _) => flyout.Hide();
        ValidateName();

        void AddMenuItem(SavedSessionTemplate template)
        {
            var item = new MenuFlyoutItem { Text = template.Name };
            item.Click += (_, _) => TemplateSelected?.Invoke(template);
            menu.Items.Add(item);
        }
    }

    public void UpdateAvailability() => SaveButton.IsEnabled = SavedSessionTemplate.CanSave(currentSessions());
}
