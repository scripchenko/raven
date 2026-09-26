using Microsoft.Win32;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class WpfMailAttachmentDialogService : IMailAttachmentDialogService
{
    public IReadOnlyList<OutgoingMailAttachment> SelectOutgoingAttachments()
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = true,
            Title = L.Instance.Get("Attach files")
        };
        if (dialog.ShowDialog() != true)
        {
            return [];
        }

        List<OutgoingMailAttachment> attachments = [];
        foreach (string path in dialog.FileNames)
        {
            attachments.Add(OutgoingMailAttachment.FromLocalFile(path));
        }

        return attachments;
    }

    public string? SelectSaveDestination(MailAttachmentInfo attachment)
    {
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            AddExtension = false,
            CheckPathExists = true,
            FileName = MailAttachmentFileName.Sanitize(attachment.FileName),
            OverwritePrompt = true,
            Title = L.Instance.Get("Save attachment")
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
