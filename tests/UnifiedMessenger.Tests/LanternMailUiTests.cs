using System.Xml.Linq;
using System.Globalization;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Branding;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.ViewModels;
using MailFolder = UnifiedMessenger.App.Models.MailFolder;

namespace UnifiedMessenger.Tests;

public sealed class LanternMailUiTests
{
    [Fact]
    public async Task MailPresentation_DefaultsToWideFolderMessageList()
    {
        UiMailProvider provider = new();
        using MailInboxViewModel viewModel = CreateViewModel(provider);

        await viewModel.ActivateAsync(Account());

        Assert.Equal(MailInboxPresentationMode.MessageList, viewModel.PresentationMode);
        Assert.True(viewModel.IsMessageListVisible);
        Assert.False(viewModel.IsMessageDetailVisible);
        Assert.Null(viewModel.SelectedMessageSummary);
    }

    [Fact]
    public async Task SelectingMessageShowsDetailAndBackPreservesFolderSelection()
    {
        UiMailProvider provider = new();
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(Account());
        MailMessageSummary summary = Assert.Single(viewModel.Messages);

        viewModel.OpenMessageCommand.Execute(summary);
        await viewModel.CurrentMessageLoadTask;

        Assert.Equal(MailInboxPresentationMode.MessageDetail, viewModel.PresentationMode);
        Assert.True(viewModel.IsMessageDetailVisible);
        Assert.Equal(summary.MessageKey, viewModel.SelectedMessageContent?.MessageKey);

        viewModel.BackToMessageListCommand.Execute(null);

        Assert.Equal(MailInboxPresentationMode.MessageList, viewModel.PresentationMode);
        Assert.True(viewModel.IsMessageListVisible);
        Assert.Equal(summary.MessageKey, viewModel.SelectedMessageSummary?.MessageKey);
        Assert.Equal(summary.MessageKey, viewModel.SelectedMessageContent?.MessageKey);
    }

    [Fact]
    public async Task ComposeTemporarilyReplacesMainContentAndCancelReturnsToPriorState()
    {
        UiMailProvider provider = new();
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(Account());

        viewModel.Compose.NewMessageCommand.Execute(null);
        Assert.Equal(MailInboxPresentationMode.Compose, viewModel.PresentationMode);
        await viewModel.Compose.CancelCommand.ExecuteAsync(null);
        Assert.Equal(MailInboxPresentationMode.MessageList, viewModel.PresentationMode);

        MailMessageSummary summary = Assert.Single(viewModel.Messages);
        viewModel.OpenMessageCommand.Execute(summary);
        await viewModel.CurrentMessageLoadTask;
        viewModel.Compose.NewMessageCommand.Execute(null);
        Assert.Equal(MailInboxPresentationMode.Compose, viewModel.PresentationMode);
        await viewModel.Compose.CancelCommand.ExecuteAsync(null);

        Assert.Equal(MailInboxPresentationMode.MessageDetail, viewModel.PresentationMode);
        Assert.Equal(summary.MessageKey, viewModel.SelectedMessageContent?.MessageKey);
    }

    [Fact]
    public async Task AccountAndFolderPresentationStatesRemainIsolated()
    {
        UiMailProvider provider = new();
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        MailAccount first = Account();
        MailAccount second = Account();

        await viewModel.ActivateAsync(first);
        MailMessageSummary firstInbox = Assert.Single(viewModel.Messages);
        viewModel.OpenMessageCommand.Execute(firstInbox);
        await viewModel.CurrentMessageLoadTask;
        Assert.Equal(MailInboxPresentationMode.MessageDetail, viewModel.PresentationMode);

        viewModel.SelectedFolder = viewModel.Folders.Single(folder => folder.Kind is MailFolderKind.Sent);
        await viewModel.CurrentFolderLoadTask;
        Assert.Equal(MailInboxPresentationMode.MessageList, viewModel.PresentationMode);
        MailMessageSummary firstSent = Assert.Single(viewModel.Messages);
        viewModel.OpenMessageCommand.Execute(firstSent);
        await viewModel.CurrentMessageLoadTask;

        await viewModel.ActivateAsync(second);
        Assert.Equal(MailInboxPresentationMode.MessageList, viewModel.PresentationMode);

        await viewModel.ActivateAsync(first);
        Assert.Equal(MailFolderKind.Sent, viewModel.SelectedFolder?.Kind);
        Assert.Equal(MailInboxPresentationMode.MessageDetail, viewModel.PresentationMode);
        Assert.Equal(firstSent.MessageKey, viewModel.SelectedMessageSummary?.MessageKey);

        viewModel.SelectedFolder = viewModel.Folders.Single(folder => folder.Kind is MailFolderKind.Inbox);
        await viewModel.CurrentFolderLoadTask;
        Assert.Equal(MailInboxPresentationMode.MessageDetail, viewModel.PresentationMode);
        Assert.Equal(firstInbox.MessageKey, viewModel.SelectedMessageSummary?.MessageKey);
    }

    [Fact]
    public void MainHeaderShowsOnlyUsefulWebRefreshAndNoBackendBadge()
    {
        XDocument window = XDocument.Load(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement[] headerButtons = window.Descendants(presentation + "Button")
            .Where(button => ((string?)button.Attribute("Command"))?.Contains(
                "Command",
                StringComparison.Ordinal) == true)
            .ToArray();
        XElement reload = Assert.Single(
            headerButtons,
            button => (string?)button.Attribute("Command") == "{Binding ReloadCommand}");
        XElement mute = Assert.Single(
            headerButtons,
            button => (string?)button.Attribute("Command") == "{Binding ToggleSelectedMuteCommand}");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement root = Assert.Single(window.Root!.Elements(presentation + "Grid"));
        XElement workspace = Assert.Single(window.Descendants(presentation + "Grid"), element =>
            (string?)element.Attribute(x + "Name") == "ServiceWorkspace");
        XElement rowDefinitions = Assert.Single(root.Elements(presentation + "Grid.RowDefinitions"));
        XElement[] rows = rowDefinitions.Elements(presentation + "RowDefinition").ToArray();
        XElement topBar = Assert.Single(root.Elements(presentation + "Border"), element =>
            (string?)element.Attribute("Grid.Row") == "0"
                && (string?)element.Attribute("Grid.ColumnSpan") == "2");
        XElement title = Assert.Single(topBar.Descendants(presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "{Binding SelectedAccountDisplayName}");
        XElement loading = Assert.Single(topBar.Descendants(presentation + "ProgressBar"), element =>
            (string?)element.Attribute("Visibility")
                == "{Binding IsLoading, Converter={StaticResource BooleanToVisibilityConverter}}");

        Assert.Equal("44", (string?)rows[0].Attribute("Height"));
        Assert.Equal("*", (string?)rows[1].Attribute("Height"));
        Assert.Null(workspace.Element(presentation + "Grid.RowDefinitions"));
        Assert.Equal("32", (string?)reload.Attribute("Width"));
        Assert.Equal("32", (string?)reload.Attribute("Height"));
        Assert.Equal("32", (string?)mute.Attribute("Width"));
        Assert.Equal("32", (string?)mute.Attribute("Height"));
        Assert.Equal(
            "{StaticResource ServiceHeaderButtonStyle}",
            (string?)reload.Attribute("Style"));
        Assert.Equal(
            "{StaticResource ServiceHeaderButtonStyle}",
            (string?)mute.Attribute("Style"));
        Assert.Equal("{DynamicResource WindowBackgroundBrush}", (string?)topBar.Attribute("Background"));
        Assert.Equal("15", (string?)title.Attribute("FontSize"));
        Assert.Equal("CharacterEllipsis", (string?)title.Attribute("TextTrimming"));
        Assert.Equal("NoWrap", (string?)title.Attribute("TextWrapping"));
        Assert.Equal("2", (string?)loading.Attribute("Height"));
        Assert.Equal("Bottom", (string?)loading.Attribute("VerticalAlignment"));
        Assert.Equal(
            "{Binding HasActiveWebView, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)reload.Attribute("Visibility"));
        Assert.DoesNotContain(headerButtons, button =>
            (string?)button.Attribute("Command") is "{Binding GoBackCommand}"
                or "{Binding GoForwardCommand}"
                or "{Binding NavigateHomeCommand}");
        Assert.DoesNotContain("SelectedAccountLabel", window.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("· WebView2", window.ToString(), StringComparison.Ordinal);
        Assert.Equal("Lantern", BrandIdentity.CreateWindowTitle("Telegram"));
    }

    [Fact]
    public void MailViewUsesNavigationPlusExclusiveListDetailAndComposeSurfaces()
    {
        XDocument view = XDocument.Load(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "MailInboxView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement navigation = Named(view, presentation, xaml, "Border", "MailNavigationPanel");
        XElement list = Named(view, presentation, xaml, "Grid", "MessageListSurface");
        XElement detail = Named(view, presentation, xaml, "Grid", "MessageDetailSurface");
        XElement compose = Named(view, presentation, xaml, "Grid", "ComposeSurface");

        Assert.NotNull(navigation);
        Assert.Equal(
            "{Binding IsMessageListVisible, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)list.Attribute("Visibility"));
        Assert.Equal(
            "{Binding IsMessageDetailVisible, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)detail.Attribute("Visibility"));
        Assert.Equal(
            "{Binding Compose.IsOpen, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)compose.Attribute("Visibility"));
        Assert.Contains("BackToMessageListCommand", detail.ToString(), StringComparison.Ordinal);
        Assert.Contains("OpenMessageCommand", list.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.Column=\"4\"", view.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MailNavigation_UsesFilledComposeAndSharedOutlineFolderSelection()
    {
        XDocument view = XDocument.Load(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "MailInboxView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement composeStyle = Assert.Single(
            view.Descendants(presentation + "Style"),
            element => (string?)element.Attribute(xaml + "Key") == "MailComposeButtonStyle");
        XElement composeButton = Assert.Single(
            view.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Content") == "Написать");
        Assert.Equal(
            "#FFE7F0FF",
            (string?)Assert.Single(
                composeStyle.Elements(presentation + "Setter"),
                setter => (string?)setter.Attribute("Property") == "Background")
                .Attribute("Value"));
        Assert.Equal(
            "{StaticResource MailComposeButtonStyle}",
            (string?)composeButton.Attribute("Style"));

        XElement folderStyle = Assert.Single(
            view.Descendants(presentation + "Style"),
            element => (string?)element.Attribute(xaml + "Key") == "MailFolderItemStyle");
        XElement selectedTrigger = Assert.Single(
            folderStyle.Descendants(presentation + "Trigger"),
            trigger =>
                (string?)trigger.Attribute("Property") == "IsSelected"
                && (string?)trigger.Attribute("Value") == "True");
        XElement hoverTrigger = Assert.Single(
            folderStyle.Descendants(presentation + "Trigger"),
            trigger =>
                (string?)trigger.Attribute("Property") == "IsMouseOver"
                && (string?)trigger.Attribute("Value") == "True");

        Assert.Equal("Transparent", TriggerValue(selectedTrigger, presentation, "Background"));
        Assert.Equal("#FF2356B8", TriggerValue(selectedTrigger, presentation, "BorderBrush"));
        Assert.Equal("1", TriggerValue(selectedTrigger, presentation, "BorderThickness"));
        Assert.Equal("#FF2356B8", TriggerValue(selectedTrigger, presentation, "Foreground"));
        Assert.Equal("#FFEAF2FF", TriggerValue(hoverTrigger, presentation, "Background"));
        Assert.DoesNotContain("#FFDDEAFF", folderStyle.ToString(), StringComparison.OrdinalIgnoreCase);

        XElement folderList = Assert.Single(
            view.Descendants(presentation + "ListBox"),
            element => (string?)element.Attribute("ItemsSource") == "{Binding Folders}");
        Assert.Equal(
            "{StaticResource MailFolderItemStyle}",
            (string?)folderList.Attribute("ItemContainerStyle"));
        Assert.All(
            new[] { MailProviderType.Gmail, MailProviderType.Yandex, MailProviderType.MailRu },
            provider => Assert.DoesNotContain(
                provider.ToString(),
                folderStyle.ToString(),
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MailDetailToolbar_IncludesMailboxActionsAndExistingCommands()
    {
        XDocument view = XDocument.Load(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "MailInboxView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement detail = Named(view, presentation, xaml, "Grid", "MessageDetailSurface");
        string detailMarkup = detail.ToString();

        Assert.Contains("BackToMessageListCommand", detailMarkup, StringComparison.Ordinal);
        Assert.Contains("SetReadStateCommand", detailMarkup, StringComparison.Ordinal);
        Assert.Contains("Compose.ReplyCommand", detailMarkup, StringComparison.Ordinal);
        Assert.Contains("Compose.ForwardCommand", detailMarkup, StringComparison.Ordinal);
        Assert.Contains("Print_Click", detailMarkup, StringComparison.Ordinal);
        Assert.Contains("CanPrintMessage", detailMarkup, StringComparison.Ordinal);
        Assert.Contains("ShowPrintAction", detailMarkup, StringComparison.Ordinal);
        Assert.Contains("MailDetailToolbarButtonStyle", detailMarkup, StringComparison.Ordinal);
        Assert.Contains("MailToolbarIconViewboxStyle", detailMarkup, StringComparison.Ordinal);
        Assert.Contains("SelectedMessageDisplayDate", detailMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Ответить\"", detailMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Переслать\"", detailMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"←  К списку\"", detailMarkup, StringComparison.Ordinal);
        Assert.Contains("ArchiveDetailCommand", detailMarkup, StringComparison.Ordinal);
        Assert.Contains("DeleteDetailCommand", detailMarkup, StringComparison.Ordinal);
        Assert.Contains("ToggleStarCommand", detailMarkup, StringComparison.Ordinal);
        Assert.Contains("OpenLabelsForDetailCommand", detailMarkup, StringComparison.Ordinal);
    }

    [Fact]
    public void MailListToolbar_HasSelectionAndMailboxActionsWithoutDuplicateFolderTitle()
    {
        XDocument view = XDocument.Load(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "MailInboxView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement list = Named(view, presentation, xaml, "Grid", "MessageListSurface");
        XElement toolbar = list.Elements(presentation + "Border").First();
        XElement refresh = Assert.Single(
            toolbar.Descendants(presentation + "Button"),
            button => (string?)button.Attribute("Command") == "{Binding RefreshCommand}");
        XElement firstRow = Assert.Single(
            list.Element(presentation + "Grid.RowDefinitions")!
                .Elements(presentation + "RowDefinition")
                .Take(1));

        Assert.Equal("44", (string?)firstRow.Attribute("Height"));
        Assert.Equal("{Binding RefreshCommand}", (string?)refresh.Attribute("Command"));
        Assert.Null(refresh.Attribute("Content"));
        Assert.Equal("Обновить", (string?)refresh.Attribute("ToolTip"));
        Assert.Equal("Обновить", (string?)refresh.Attribute("AutomationProperties.Name"));
        XElement refreshPath = Assert.Single(refresh.Descendants(presentation + "Path"));
        Assert.Equal("{StaticResource MailActionRefreshGeometry}", (string?)refreshPath.Attribute("Data"));
        Assert.Equal("1.8", (string?)refreshPath.Attribute("StrokeThickness"));
        Assert.DoesNotContain("SelectedFolder.DisplayName", list.ToString(), StringComparison.Ordinal);
        Assert.Contains("SelectAllLoadedCommand", toolbar.ToString(), StringComparison.Ordinal);
        Assert.Contains("ArchiveSelectedCommand", toolbar.ToString(), StringComparison.Ordinal);
        Assert.Contains("DeleteSelectedCommand", toolbar.ToString(), StringComparison.Ordinal);
        Assert.Contains("OpenLabelsForSelectionCommand", toolbar.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MailboxToolbars_AreIconFirstAccessibleAndReuseActionGeometry()
    {
        XDocument view = XDocument.Load(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "MailInboxView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement list = Named(view, presentation, xaml, "Grid", "MessageListSurface");
        XElement detail = Named(view, presentation, xaml, "Grid", "MessageDetailSurface");
        XElement listToolbar = list.Elements(presentation + "Border").First();
        XElement detailToolbar = detail.Elements(presentation + "Border").First();
        string[] listCommands =
        [
            "RefreshCommand",
            "ArchiveSelectedCommand",
            "DeleteSelectedCommand",
            "MarkSelectedReadCommand",
            "MarkSelectedUnreadCommand",
            "ToggleSelectedStarCommand",
            "OpenLabelsForSelectionCommand",
            "ClearSelectionCommand"
        ];

        foreach (string command in listCommands)
        {
            XElement button = Assert.Single(
                listToolbar.Descendants(presentation + "Button"),
                candidate => (string?)candidate.Attribute("Command") == $"{{Binding {command}}}");
            Assert.Null(button.Attribute("Content"));
            Assert.False(string.IsNullOrWhiteSpace((string?)button.Attribute("ToolTip")));
            Assert.False(string.IsNullOrWhiteSpace((string?)button.Attribute("AutomationProperties.Name")));
        }

        Assert.DoesNotContain("Content=\"Архив\"", listToolbar.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Удалить\"", listToolbar.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Прочитано\"", listToolbar.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Не прочитано\"", listToolbar.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Снять выбор\"", listToolbar.ToString(), StringComparison.Ordinal);

        AssertSharedGeometry("ArchiveSelectedCommand", "ArchiveDetailCommand");
        AssertSharedGeometry("DeleteSelectedCommand", "DeleteDetailCommand");
        AssertSharedGeometry("OpenLabelsForSelectionCommand", "OpenLabelsForDetailCommand");

        void AssertSharedGeometry(string listCommand, string detailCommand)
        {
            XElement listButton = Assert.Single(
                listToolbar.Descendants(presentation + "Button"),
                candidate => (string?)candidate.Attribute("Command") == $"{{Binding {listCommand}}}");
            XElement detailButton = Assert.Single(
                detailToolbar.Descendants(presentation + "Button"),
                candidate => (string?)candidate.Attribute("Command") == $"{{Binding {detailCommand}}}");
            Assert.Equal(
                (string?)Assert.Single(listButton.Descendants(presentation + "Path")).Attribute("Data"),
                (string?)Assert.Single(detailButton.Descendants(presentation + "Path")).Attribute("Data"));
        }
    }

    [Fact]
    public void MailListRows_CenterSelectionAndStarInPaddedCompactColumns()
    {
        XDocument view = XDocument.Load(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "MailInboxView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement list = Named(view, presentation, xaml, "Grid", "MessageListSurface");
        XElement messageList = Assert.Single(list.Elements(presentation + "ListBox"));
        XElement row = messageList.Element(presentation + "ListBox.ItemTemplate")!
            .Element(presentation + "DataTemplate")!
            .Element(presentation + "Grid")!;
        XElement[] columns = row.Element(presentation + "Grid.ColumnDefinitions")!
            .Elements(presentation + "ColumnDefinition")
            .ToArray();
        XElement selection = Assert.Single(row.Elements(presentation + "CheckBox"));
        XElement star = Assert.Single(
            row.Elements(presentation + "Button"),
            button => ((string?)button.Attribute("Command"))?.Contains("ToggleStarCommand", StringComparison.Ordinal) == true);

        Assert.Equal(3, columns.Length);
        Assert.All(columns.Take(2), column =>
        {
            Assert.True(double.TryParse(
                (string?)column.Attribute("Width"),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out double width));
            Assert.InRange(width, 40, 52);
        });
        Assert.Equal("Center", (string?)selection.Attribute("HorizontalAlignment"));
        Assert.Equal("Center", (string?)selection.Attribute("VerticalAlignment"));
        Assert.NotNull(selection.Attribute("Margin"));
        Assert.Equal("Center", (string?)star.Attribute("HorizontalAlignment"));
        Assert.Equal("Center", (string?)star.Attribute("VerticalAlignment"));
        Assert.NotNull(star.Attribute("Margin"));
        Assert.Equal("0", (string?)star.Attribute("Padding"));

        XElement starText = Assert.Single(star.Elements(presentation + "TextBlock"));
        Assert.Equal("Center", (string?)starText.Attribute("HorizontalAlignment"));
        Assert.Equal("Center", (string?)starText.Attribute("VerticalAlignment"));
        Assert.Contains("Value=\"☆\"", starText.ToString(), StringComparison.Ordinal);
        Assert.Contains("Value=\"★\"", starText.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MailDetailToolbar_IsLeftAlignedInMailboxAndComposeActionOrder()
    {
        XDocument view = XDocument.Load(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "MailInboxView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement detail = Named(view, presentation, xaml, "Grid", "MessageDetailSurface");
        XElement toolbar = detail.Elements(presentation + "Border").First();
        XElement stack = Assert.Single(toolbar.Elements(presentation + "StackPanel"));
        XElement[] buttons = stack.Elements(presentation + "Button").ToArray();

        Assert.Collection(
            buttons,
            button => Assert.Equal("{Binding BackToMessageListCommand}", (string?)button.Attribute("Command")),
            button => Assert.Equal("{Binding ArchiveDetailCommand}", (string?)button.Attribute("Command")),
            button => Assert.Equal("{Binding DeleteDetailCommand}", (string?)button.Attribute("Command")),
            button => Assert.Equal("{Binding ToggleStarCommand}", (string?)button.Attribute("Command")),
            button => Assert.Equal("{Binding OpenLabelsForDetailCommand}", (string?)button.Attribute("Command")),
            button => Assert.Equal("{Binding SetReadStateCommand}", (string?)button.Attribute("Command")),
            button => Assert.Equal("{Binding Compose.ReplyCommand}", (string?)button.Attribute("Command")),
            button => Assert.Equal("{Binding Compose.ForwardCommand}", (string?)button.Attribute("Command")),
            button => Assert.Equal("Print_Click", (string?)button.Attribute("Click")));
        Assert.Equal("Left", (string?)stack.Attribute("HorizontalAlignment"));
        Assert.Equal("Horizontal", (string?)stack.Attribute("Orientation"));
    }

    [Fact]
    public void MailDetailToolbar_UsesNormalizedUnclippedIconPresentation()
    {
        XDocument view = XDocument.Load(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "MailInboxView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement iconStyle = Assert.Single(
            view.Descendants(presentation + "Style"),
            style => (string?)style.Attribute(xaml + "Key") == "MailToolbarIconViewboxStyle");
        XElement detail = Named(view, presentation, xaml, "Grid", "MessageDetailSurface");
        XElement toolbar = detail.Elements(presentation + "Border").First();
        XElement stack = Assert.Single(toolbar.Elements(presentation + "StackPanel"));
        XElement[] buttons = stack.Elements(presentation + "Button").ToArray();

        Assert.Contains(
            iconStyle.Elements(presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Width"
                && (string?)setter.Attribute("Value") == "22");
        Assert.Contains(
            iconStyle.Elements(presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Height"
                && (string?)setter.Attribute("Value") == "22");
        Assert.Contains(
            iconStyle.Elements(presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Stretch"
                && (string?)setter.Attribute("Value") == "Uniform");
        XElement[] vectorButtons = buttons
            .Where(button => button.Elements(presentation + "Viewbox").Any())
            .ToArray();
        Assert.Equal(8, vectorButtons.Length);
        Assert.All(vectorButtons, button =>
        {
            XElement viewbox = Assert.Single(button.Elements(presentation + "Viewbox"));
            XElement canvas = Assert.Single(viewbox.Elements(presentation + "Canvas"));
            Assert.Equal("{StaticResource MailToolbarIconViewboxStyle}", (string?)viewbox.Attribute("Style"));
            Assert.Equal("24", (string?)canvas.Attribute("Width"));
            Assert.Equal("24", (string?)canvas.Attribute("Height"));
        });
    }

    [Fact]
    public void RemoteImagesBanner_OffersExplicitOneTimeAlwaysAndRevokeActions()
    {
        XDocument view = XDocument.Load(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "MailInboxView.xaml"));
        string markup = view.ToString();

        Assert.Contains("Content=\"{Binding RemoteImagesButtonText}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Всегда показывать от этого отправителя", markup, StringComparison.Ordinal);
        Assert.Contains("Не показывать автоматически от этого отправителя", markup, StringComparison.Ordinal);
        Assert.Contains("AlwaysShowRemoteImagesFromSender_Click", markup, StringComparison.Ordinal);
        Assert.Contains("RevokeRemoteImagesFromSender_Click", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetailCommandsReflectReadStateAndDraftCapability()
    {
        UiMailProvider provider = new();
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(Account());
        MailMessageSummary inbox = Assert.Single(viewModel.Messages);
        viewModel.OpenMessageCommand.Execute(inbox);
        await viewModel.CurrentMessageLoadTask;

        Assert.True(viewModel.BackToMessageListCommand.CanExecute(null));
        Assert.True(viewModel.Compose.ReplyCommand.CanExecute(viewModel.SelectedMessageContent));
        Assert.True(viewModel.Compose.ForwardCommand.CanExecute(viewModel.SelectedMessageContent));

        viewModel.SelectedFolder = viewModel.Folders.Single(folder => folder.Kind is MailFolderKind.Drafts);
        await viewModel.CurrentFolderLoadTask;
        MailMessageSummary draft = Assert.Single(viewModel.Messages);
        viewModel.OpenMessageCommand.Execute(draft);
        await viewModel.CurrentMessageLoadTask;

        Assert.False(viewModel.ShowReadStateAction);
        Assert.False(viewModel.SetReadStateCommand.CanExecute(null));
    }

    [Fact]
    public async Task DetailDateUsesCurrentCultureAndOmitsSeconds()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            UiMailProvider provider = new();
            using MailInboxViewModel viewModel = CreateViewModel(provider);
            await viewModel.ActivateAsync(Account());
            viewModel.OpenMessageCommand.Execute(Assert.Single(viewModel.Messages));
            await viewModel.CurrentMessageLoadTask;

            string expected = viewModel.SelectedMessageContent!.ReceivedAt
                .ToLocalTime()
                .ToString("ddd, d MMM, HH:mm", CultureInfo.CurrentCulture);
            Assert.Equal(expected, viewModel.SelectedMessageDisplayDate);
            Assert.DoesNotMatch(@"\d{2}:\d{2}:\d{2}", viewModel.SelectedMessageDisplayDate);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void StartupProgressUsesLanternGradientAndRoundedNeutralTrack()
    {
        string startup = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "StartupWindow.xaml"));

        Assert.Contains("#FF20D7EE", startup, StringComparison.Ordinal);
        Assert.Contains("#FF2D7DFF", startup, StringComparison.Ordinal);
        Assert.Contains("#FFE9EEF4", startup, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"3.5\"", startup, StringComparison.Ordinal);
    }

    private static XElement Named(
        XDocument document,
        XNamespace presentation,
        XNamespace xaml,
        string elementName,
        string name) =>
        Assert.Single(
            document.Descendants(presentation + elementName),
            element => (string?)element.Attribute(xaml + "Name") == name);

    private static string? TriggerValue(
        XElement trigger,
        XNamespace presentation,
        string property) =>
        Assert.Single(
            trigger.Elements(presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == property)
        .Attribute("Value")
        ?.Value;

    private static MailInboxViewModel CreateViewModel(UiMailProvider provider) =>
        new(new UiMailProviderFactory(provider));

    private static MailAccount Account() =>
        new()
        {
            Id = Guid.NewGuid(),
            Provider = MailProviderType.Yandex,
            EmailAddress = "account@example.test",
            CredentialKey = Guid.NewGuid().ToString("N"),
            AuthenticationKind = MailAuthenticationKind.Password,
            IsEnabled = true
        };

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }

    private sealed class UiMailProviderFactory(UiMailProvider provider) : IMailReadProviderFactory
    {
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class UiMailProvider : IMailReadProvider
    {
        public bool Supports(MailProviderType providerType) => true;

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
            MailAccount account,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailFolder>>
            ([
                MailFolderCatalog.Inbox(),
                MailFolderCatalog.Create(MailFolderKind.Sent, "SENT"),
                MailFolderCatalog.Create(MailFolderKind.Drafts, "DRAFT")
            ]);

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
            MailAccount account,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            GetPageAsync(account, MailFolderCatalog.Inbox(), continuationToken, pageSize, cancellationToken);

        public Task<MailPage<MailMessageSummary>> GetPageAsync(
            MailAccount account,
            MailFolder folder,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            string key = $"{account.Id:N}:{folder.Kind}";
            MailMessageSummary summary = new(
                key,
                "Subject",
                "Sender",
                "sender@example.test",
                DateTimeOffset.UtcNow,
                "Preview",
                false);
            return Task.FromResult(new MailPage<MailMessageSummary>([summary], null));
        }

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            string messageKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Content(messageKey));

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            MailFolder folder,
            string messageKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Content(messageKey));

        private static MailMessageContent Content(string key) =>
            new(
                key,
                "Subject",
                "Sender",
                "sender@example.test",
                "account@example.test",
                DateTimeOffset.UtcNow,
                MailMessageBodyKind.PlainText,
                "Body",
                [],
                false,
                false);
    }
}
