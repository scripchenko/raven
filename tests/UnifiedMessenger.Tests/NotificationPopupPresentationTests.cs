using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using UnifiedMessenger.App.Services.Notifications;
using UnifiedMessenger.App.Views;

namespace UnifiedMessenger.Tests;

public sealed class NotificationPopupPresentationTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("Telegram", "telegram.png")]
    [InlineData("WhatsApp", "whatsapp.png")]
    [InlineData("MAX", "max.png")]
    [InlineData("Почта", null)]
    public void KnownSource_UsesCorrectLabelAndLocalIcons(string source, string? asset)
    {
        RunSta(() =>
        {
            NotificationPopupDisplayModel model = CreateModel(source);
            NotificationPopupWindow window = new(model);
            try
            {
                ArrangeCard(window);
                TextBlock label = Assert.IsType<TextBlock>(window.FindName("SourceLabel"));
                BindingOperations.GetBindingExpression(label, TextBlock.TextProperty)!.UpdateTarget();
                Assert.Equal(source, label.Text);
                Assert.Same(model, window.DataContext);

                Image headerIcon = Assert.IsType<Image>(window.FindName("HeaderSourceIcon"));
                Image contentIcon = Assert.IsType<Image>(window.FindName("ContentSourceIcon"));
                Assert.NotNull(headerIcon.Source);
                Assert.Same(headerIcon.Source, contentIcon.Source);
                if (asset is null)
                {
                    Assert.IsType<DrawingImage>(headerIcon.Source);
                }
                else
                {
                    Assert.IsAssignableFrom<BitmapSource>(headerIcon.Source);
                    Assert.EndsWith(asset, headerIcon.Source.ToString(), StringComparison.Ordinal);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void GmailPopup_UsesGmailHeaderPreviewAndSenderInitialsAvatar()
    {
        RunSta(() =>
        {
            NotificationPopupDisplayModel model = new(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Gmail • mail@example.test",
                "Анна Смирнова",
                "Статус проекта",
                "Короткий preview",
                NotificationPopupBrand.Gmail,
                "АС");
            NotificationPopupWindow window = new(model);
            try
            {
                ArrangeCard(window);

                TextBlock sourceLabel = Assert.IsType<TextBlock>(window.FindName("SourceLabel"));
                Image headerIcon = Assert.IsType<Image>(window.FindName("HeaderSourceIcon"));
                Image contentIcon = Assert.IsType<Image>(window.FindName("ContentSourceIcon"));
                Border avatar = Assert.IsType<Border>(window.FindName("SenderInitialsAvatar"));
                TextBlock preview = Assert.IsType<TextBlock>(window.FindName("NotificationPreview"));

                Assert.Equal("Gmail • mail@example.test", sourceLabel.Text);
                Assert.EndsWith("gmail.png", headerIcon.Source.ToString(), StringComparison.Ordinal);
                Assert.Equal(Visibility.Collapsed, contentIcon.Visibility);
                Assert.Equal(Visibility.Visible, avatar.Visibility);
                Assert.Equal("АС", Assert.IsType<TextBlock>(avatar.Child).Text);
                Assert.Equal("Короткий preview", preview.Text);
                Assert.Equal(Visibility.Visible, preview.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void GmailPopup_WithoutUsableSenderName_UsesGmailIconFallback()
    {
        RunSta(() =>
        {
            NotificationPopupDisplayModel model = new(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Gmail",
                "sender@example.test",
                "(без темы)",
                Brand: NotificationPopupBrand.Gmail);
            NotificationPopupWindow window = new(model);
            try
            {
                ArrangeCard(window);

                Image headerIcon = Assert.IsType<Image>(window.FindName("HeaderSourceIcon"));
                Image contentIcon = Assert.IsType<Image>(window.FindName("ContentSourceIcon"));
                Border avatar = Assert.IsType<Border>(window.FindName("SenderInitialsAvatar"));
                TextBlock preview = Assert.IsType<TextBlock>(window.FindName("NotificationPreview"));

                Assert.EndsWith("gmail.png", headerIcon.Source.ToString(), StringComparison.Ordinal);
                Assert.EndsWith("gmail.png", contentIcon.Source.ToString(), StringComparison.Ordinal);
                Assert.Equal(Visibility.Visible, contentIcon.Visibility);
                Assert.Equal(Visibility.Collapsed, avatar.Visibility);
                Assert.Equal(Visibility.Collapsed, preview.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void MailRuPopup_UsesMailRuIconAndSenderInitialsAvatar()
    {
        RunSta(() =>
        {
            NotificationPopupDisplayModel model = new(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Почта Mail.ru • mail@example.test",
                "Иван Петров",
                "Тема письма",
                "Короткий preview",
                NotificationPopupBrand.MailRu,
                "ИП");
            NotificationPopupWindow window = new(model);
            try
            {
                ArrangeCard(window);

                TextBlock sourceLabel = Assert.IsType<TextBlock>(window.FindName("SourceLabel"));
                Image headerIcon = Assert.IsType<Image>(window.FindName("HeaderSourceIcon"));
                Image contentIcon = Assert.IsType<Image>(window.FindName("ContentSourceIcon"));
                Border avatar = Assert.IsType<Border>(window.FindName("SenderInitialsAvatar"));
                TextBlock preview = Assert.IsType<TextBlock>(window.FindName("NotificationPreview"));

                Assert.Equal("Почта Mail.ru • mail@example.test", sourceLabel.Text);
                Assert.EndsWith("mailru.png", headerIcon.Source.ToString(), StringComparison.Ordinal);
                Assert.Equal(Visibility.Collapsed, contentIcon.Visibility);
                Assert.Equal(Visibility.Visible, avatar.Visibility);
                Assert.Equal("ИП", Assert.IsType<TextBlock>(avatar.Child).Text);
                Assert.Equal("Короткий preview", preview.Text);
                Assert.Equal(Visibility.Visible, preview.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("Telegram", NotificationPopupBrand.Telegram, "telegram.png")]
    [InlineData("WhatsApp", NotificationPopupBrand.WhatsApp, "whatsapp.png")]
    public void MessengerPopup_KeepsHeaderBrandAndUsesRightSideInitialsAvatar(
        string serviceName,
        NotificationPopupBrand brand,
        string iconAsset)
    {
        RunSta(() =>
        {
            NotificationPopupDisplayModel model = new(
                Guid.NewGuid(),
                Guid.NewGuid(),
                serviceName,
                "Иван Петров",
                "Текст сообщения",
                Brand: brand,
                SenderAvatarInitials: "ИП",
                SenderAvatarIdentity: "Иван Петров");
            NotificationPopupWindow window = new(model);
            try
            {
                ArrangeCard(window);

                Image headerIcon = Assert.IsType<Image>(window.FindName("HeaderSourceIcon"));
                Image contentIcon = Assert.IsType<Image>(window.FindName("ContentSourceIcon"));
                Border avatar = Assert.IsType<Border>(window.FindName("SenderInitialsAvatar"));

                Assert.EndsWith(iconAsset, headerIcon.Source.ToString(), StringComparison.Ordinal);
                Assert.Equal(Visibility.Collapsed, contentIcon.Visibility);
                Assert.Equal(Visibility.Visible, avatar.Visibility);
                Assert.Equal("ИП", Assert.IsType<TextBlock>(avatar.Child).Text);
                Assert.Equal("Иван Петров", Assert.IsType<TextBlock>(window.FindName("NotificationTitle")).Text);
                Assert.Equal("Текст сообщения", Assert.IsType<TextBlock>(window.FindName("NotificationBody")).Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("Telegram")]
    [InlineData("WhatsApp")]
    [InlineData("MAX")]
    [InlineData("Почта")]
    public void ShortNotification_HasCompactVisibleCardWithoutClipping(string source)
    {
        RunSta(() =>
        {
            NotificationPopupWindow window = new(CreateModel(source));
            try
            {
                Border card = ArrangeCard(window);
                Assert.InRange(card.ActualWidth, 354.9, 355.1);
                Assert.InRange(card.ActualHeight, 84, 90);
                Assert.Equal(20, Assert.IsType<Image>(window.FindName("HeaderSourceIcon")).Height);
                Assert.Equal(36, Assert.IsType<Image>(window.FindName("ContentSourceIcon")).Height);

                foreach (string name in new[] { "NotificationTitle", "NotificationBody" })
                {
                    TextBlock text = Assert.IsType<TextBlock>(window.FindName(name));
                    Assert.True(text.ActualWidth > 250);
                    Assert.True(text.ActualHeight >= text.LineHeight);
                    Assert.True(text.TranslatePoint(new Point(0, text.ActualHeight), card).Y <= card.ActualHeight);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TextPolish_RaisesContentWithoutMovingCardOrIcons()
    {
        RunSta(() =>
        {
            NotificationPopupWindow window = new(CreateModel("Telegram"));
            try
            {
                Border card = ArrangeCard(window);
                StackPanel textPanel = Assert.IsType<StackPanel>(window.FindName("NotificationText"));
                TextBlock title = Assert.IsType<TextBlock>(window.FindName("NotificationTitle"));
                TextBlock body = Assert.IsType<TextBlock>(window.FindName("NotificationBody"));
                Image icon = Assert.IsType<Image>(window.FindName("ContentSourceIcon"));
                Size cardSize = card.RenderSize;
                Point iconPosition = icon.TranslatePoint(new Point(), card);
                double titleTop = title.TranslatePoint(new Point(), card).Y;
                double bodyTop = body.TranslatePoint(new Point(), card).Y;

                Assert.Equal(-3, Assert.IsType<TranslateTransform>(textPanel.RenderTransform).Y);
                Assert.Equal(-1, Assert.IsType<TranslateTransform>(body.RenderTransform).Y);
                Assert.Equal(titleTop + title.ActualHeight, bodyTop, precision: 3);

                textPanel.RenderTransform = Transform.Identity;
                body.RenderTransform = Transform.Identity;
                card.UpdateLayout();

                Assert.Equal(3, title.TranslatePoint(new Point(), card).Y - titleTop, precision: 3);
                Assert.Equal(4, body.TranslatePoint(new Point(), card).Y - bodyTop, precision: 3);
                Assert.Equal(cardSize, card.RenderSize);
                Assert.Equal(iconPosition, icon.TranslatePoint(new Point(), card));
                Assert.Equal(14, title.FontSize);
                Assert.Equal(12.5, body.FontSize);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void LongNotification_UsesBoundedWrappedTextWithoutChangingContent()
    {
        RunSta(() =>
        {
            NotificationPopupDisplayModel model = CreateModel("Telegram") with
            {
                Title = string.Join(' ', Enumerable.Repeat("Тестовый заголовок", 20)),
                Body = string.Join(' ', Enumerable.Repeat("Тестовый текст", 50))
            };
            NotificationPopupWindow window = new(model);
            try
            {
                Border card = ArrangeCard(window);
                TextBlock title = Assert.IsType<TextBlock>(window.FindName("NotificationTitle"));
                TextBlock body = Assert.IsType<TextBlock>(window.FindName("NotificationBody"));
                Assert.Equal(model.Title, title.Text);
                Assert.Equal(model.Body, body.Text);
                Assert.InRange(card.ActualHeight + card.Margin.Top + card.Margin.Bottom, 92, window.MaxHeight);
                Assert.True(title.ActualHeight <= 54);
                Assert.True(body.ActualHeight <= 96);
                Assert.Equal(TextWrapping.Wrap, body.TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, body.TextTrimming);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Popup_UsesLightCompactReferenceStyle()
    {
        XDocument document = LoadPopupXaml();
        XElement card = FindNamed(document, "PopupCard");
        Assert.Equal("363", document.Root!.Attribute("Width")!.Value);
        Assert.Equal("92", document.Root.Attribute("MinHeight")!.Value);
        Assert.Equal("84", card.Attribute("MinHeight")!.Value);
        Assert.Equal("14,10", card.Attribute("Padding")!.Value);
        Assert.Equal("#FFFAFAFA", card.Attribute("Background")!.Value);
        Assert.Equal("#FFD9D9D9", card.Attribute("BorderBrush")!.Value);
        Assert.Equal("1", card.Attribute("BorderThickness")!.Value);
        Assert.Equal("2", card.Attribute("CornerRadius")!.Value);
        Assert.Equal("14", FindNamed(document, "NotificationTitle").Attribute("FontSize")!.Value);
        Assert.Equal("12.5", FindNamed(document, "NotificationBody").Attribute("FontSize")!.Value);
        Assert.DoesNotContain("#F92A2D33", document.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("#FF71B7FF", document.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Header_HasStaticTimestampAndOnlyFunctionalCloseButton()
    {
        XDocument document = LoadPopupXaml();
        XElement button = Assert.Single(document.Descendants(Presentation + "Button"));
        Assert.Equal("×", button.Attribute("Content")!.Value);
        Assert.Equal("26", button.Attribute("Width")!.Value);
        Assert.Equal("26", button.Attribute("Height")!.Value);
        Assert.Equal("18", button.Attribute("FontSize")!.Value);
        XElement closeStyle = document.Descendants(Presentation + "Style")
            .Single(element => element.Attribute(Xaml + "Key")?.Value == "PopupCloseButtonStyle");
        Assert.Equal("#FF404040", closeStyle.Elements(Presentation + "Setter")
            .Single(element => element.Attribute("Property")?.Value == "Foreground").Attribute("Value")!.Value);
        Assert.Equal("CloseButton_Click", button.Attribute("Click")!.Value);
        Assert.Equal("Popup_MouseLeftButtonUp", FindNamed(document, "PopupCard").Attribute("MouseLeftButtonUp")!.Value);
        Assert.Contains(document.Descendants(Presentation + "TextBlock"),
            element => element.Attribute("Text")?.Value == "{loc:Text Key='• now'}");
        Assert.Equal("{Binding ServiceName}", FindNamed(document, "SourceLabel").Attribute("Text")!.Value);
        Assert.Equal("Normal", FindNamed(document, "SourceLabel").Attribute("FontWeight")!.Value);
    }

    [Fact]
    public void Window_StillDoesNotActivateOrEnterTaskbar()
    {
        XElement window = LoadPopupXaml().Root!;
        Assert.Equal("True", window.Attribute("Topmost")!.Value);
        Assert.Equal("False", window.Attribute("ShowInTaskbar")!.Value);
        Assert.Equal("False", window.Attribute("ShowActivated")!.Value);
        Assert.Equal("False", window.Attribute("Focusable")!.Value);
        Assert.Equal("None", window.Attribute("WindowStyle")!.Value);
    }

    [Fact]
    public void ExistingTimeoutClickAndStackLifecycle_ArePreserved()
    {
        string windowCode = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "NotificationPopupWindow.xaml.cs"));
        string serviceCode = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Services", "Notifications", "WpfNotificationPopupService.cs"));
        Assert.Contains("Interval = TimeSpan.FromSeconds(6)", windowCode, StringComparison.Ordinal);
        Assert.Contains("PopupClicked?.Invoke(this, EventArgs.Empty)", windowCode, StringComparison.Ordinal);
        Assert.Contains("PopupClosed?.Invoke(this, EventArgs.Empty)", windowCode, StringComparison.Ordinal);
        Assert.Contains("HasButtonAncestor(eventArgs.OriginalSource", windowCode, StringComparison.Ordinal);
        Assert.Contains("MaximumVisiblePopups = 3", serviceCode, StringComparison.Ordinal);
        Assert.Contains("ScreenMargin = 16", serviceCode, StringComparison.Ordinal);
        Assert.Contains("PopupGap = 0", serviceCode, StringComparison.Ordinal);
        Assert.Contains("SystemParameters.WorkArea", serviceCode, StringComparison.Ordinal);
        Assert.Contains("_displayOrder.AsEnumerable().Reverse()", serviceCode, StringComparison.Ordinal);
        Assert.Contains("bottomOffset += height + PopupGap", serviceCode, StringComparison.Ordinal);
        Assert.Equal("4", FindNamed(LoadPopupXaml(), "PopupCard").Attribute("Margin")!.Value);
    }

    private static Border ArrangeCard(NotificationPopupWindow window)
    {
        Border card = Assert.IsType<Border>(window.Content);
        card.Measure(new Size(window.Width, double.PositiveInfinity));
        card.Arrange(new Rect(0, 0, window.Width, card.DesiredSize.Height));
        card.UpdateLayout();
        return card;
    }

    private static NotificationPopupDisplayModel CreateModel(string source) =>
        new(Guid.NewGuid(), Guid.NewGuid(), source, "Новое письмо", "Получено новое письмо");

    private static XDocument LoadPopupXaml() => XDocument.Load(FindRepositoryFile(
        "src", "UnifiedMessenger.App", "Views", "NotificationPopupWindow.xaml"));

    private static XElement FindNamed(XDocument document, string name) =>
        document.Descendants().Single(element => element.Attribute(Xaml + "Name")?.Value == name);

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }
}
