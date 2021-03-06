﻿using System.IO;
using System.Threading.Tasks;

namespace EcommerceApi.Services
{
    public interface IEmailSender
    {
        Task SendEmailAsync(string toEmail, string subject, string htmlMessage, string textMessage = null, Stream attachment = null, string attachmentName = null, bool ccAdmins = false);
    }
}
