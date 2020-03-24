﻿using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Remotely.Server.Auth;
using Remotely.Server.Services;
using Remotely.Shared.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Remotely.Server.API
{
    [Route("api/[controller]")]
    [ApiController]
    [ServiceFilter(typeof(ApiAuthorizationFilter))]
    public class AlertsController : ControllerBase
    {
        public AlertsController(DataService dataService, IEmailSenderEx emailSender)
        {
            DataService = dataService;
            EmailSender = emailSender;
        }

        private DataService DataService { get; }
        private IEmailSenderEx EmailSender { get; }

        [HttpPost("Create")]
        [ServiceFilter(typeof(ApiAuthorizationFilter))]
        public async Task<IActionResult> Create(AlertOptions alertOptions)
        {
            Request.Headers.TryGetValue("OrganizationID", out var orgID);

            DataService.WriteEvent("Alert created.  Alert Options: " + JsonSerializer.Serialize(alertOptions), orgID);

            if (alertOptions.ShouldAlert)
            {
                try
                {
                    var alert = new Alert()
                    {
                        CreatedOn = DateTimeOffset.Now,
                        DeviceID = alertOptions.AlertDeviceID,
                        Message = alertOptions.AlertMessage,
                        OrganizationID = orgID
                    };
                    await DataService.AddAlert(alert);
                }
                catch (Exception ex)
                {
                    DataService.WriteEvent(ex, orgID);
                }
            }

            if (alertOptions.ShouldEmail)
            {
                try
                {
                    await EmailSender.SendEmailAsync(alertOptions.EmailTo,
                  alertOptions.EmailSubject,
                  alertOptions.EmailBody,
                  orgID);
                }
                catch (Exception ex)
                {
                    DataService.WriteEvent(ex, orgID);
                }
              
            }

            if (alertOptions.ShouldSendApiRequest)
            {
                try
                {
                    var httpRequest = WebRequest.CreateHttp(alertOptions.ApiRequestUrl);
                    httpRequest.Method = alertOptions.ApiRequestMethod;
                    httpRequest.ContentType = "application/json";
                    foreach (var header in alertOptions.ApiRequestHeaders)
                    {
                        httpRequest.Headers.Add(header.Key, header.Value);
                    }
                    using (var rs = httpRequest.GetRequestStream())
                    using (var sw = new StreamWriter(rs))
                    {
                        sw.Write(alertOptions.ApiRequestBody);
                    }
                    var response = (HttpWebResponse)httpRequest.GetResponse();
                    DataService.WriteEvent($"Alert API Response Status: {response.StatusCode}.", orgID);
                }
                catch (Exception ex)
                {
                    DataService.WriteEvent(ex, orgID);
                }
              
            }

            return Ok();
        }

        [HttpPost("Delete/{alertID}")]
        [ServiceFilter(typeof(ApiAuthorizationFilter))]
        public async Task<IActionResult> Delete(string alertID)
        {
            Request.Headers.TryGetValue("OrganizationID", out var orgID);

            var alert = await DataService.GetAlert(alertID);

            if (alert?.OrganizationID == orgID)
            {
                await DataService.DeleteAlert(alert);

                return Ok();
            }

            return Unauthorized();
        }
    }
}
