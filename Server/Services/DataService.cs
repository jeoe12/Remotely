﻿using Remotely.Shared.Models;
using Remotely.Shared.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remotely.Shared.ViewModels.Organization;
using Remotely.Server.Data;
using Microsoft.Extensions.Hosting;
using System.Threading.Tasks;
using Remotely.Server.Auth;

namespace Remotely.Server.Services
{
    public class DataService
    {
        public DataService(ApplicationDbContext context, ApplicationConfig appConfig, IHostEnvironment hostEnvironment)
        {
            RemotelyContext = context;
            AppConfig = appConfig;
            HostEnvironment = hostEnvironment;
        }

        private ApplicationConfig AppConfig { get; }
        private IHostEnvironment HostEnvironment { get; }
        private ApplicationDbContext RemotelyContext { get; }

        public bool AddDeviceGroup(string orgID, DeviceGroup deviceGroup, out string deviceGroupID, out string errorMessage)
        {
            deviceGroupID = null;
            errorMessage = null;

            var organization = RemotelyContext.Organizations
                .Include(x => x.DeviceGroups)
                .FirstOrDefault(x => x.ID == orgID);
          
            if (RemotelyContext.DeviceGroups.Any(x => 
                x.OrganizationID == orgID &&
                x.Name.ToLower() == deviceGroup.Name.ToLower()))
            {
                errorMessage = "Device group already exists.";
                return false;
            }

            var newDeviceGroup = new DeviceGroup()
            {
                Name = deviceGroup.Name,
                Organization = organization,
                OrganizationID = orgID
            };

            organization.DeviceGroups.Add(newDeviceGroup);
            RemotelyContext.SaveChanges();
            deviceGroupID = newDeviceGroup.ID;
            return true;
        }

        public InviteLink AddInvite(string orgID, Invite invite)
        {
            invite.InvitedUser = invite.InvitedUser.ToLower();

            var organization = RemotelyContext.Organizations
                .Include(x => x.InviteLinks)
                .FirstOrDefault(x => x.ID == orgID);

            var newInvite = new InviteLink()
            {
                DateSent = DateTime.Now,
                InvitedUser = invite.InvitedUser,
                IsAdmin = invite.IsAdmin,
                Organization = organization,
                OrganizationID = organization.ID,
                ResetUrl = invite.ResetUrl
            };
            organization.InviteLinks.Add(newInvite);
            RemotelyContext.SaveChanges();
            return newInvite;
        }

        public void AddOrUpdateCommandContext(CommandContext commandContext)
        {
            var existingContext = RemotelyContext.CommandContexts.Find(commandContext.ID);
            if (existingContext != null)
            {
                var entry = RemotelyContext.Entry(existingContext);
                entry.CurrentValues.SetValues(commandContext);
                entry.State = EntityState.Modified;
            }
            else
            {
                RemotelyContext.CommandContexts.Add(commandContext);
            }
            RemotelyContext.SaveChanges();
        }

        public bool AddOrUpdateDevice(Device device, out Device updatedDevice)
        {
            device.LastOnline = DateTime.Now;

            var existingDevice = RemotelyContext.Devices.Find(device.ID);
            if (existingDevice != null)
            {
                existingDevice.CurrentUser = device.CurrentUser;
                existingDevice.DeviceName = device.DeviceName;
                existingDevice.Drives = device.Drives;
                existingDevice.CpuUtilization = device.CpuUtilization;
                existingDevice.UsedMemory = device.UsedMemory;
                existingDevice.UsedStorage = device.UsedStorage;
                existingDevice.Is64Bit = device.Is64Bit;
                existingDevice.IsOnline = true;
                existingDevice.OSArchitecture = device.OSArchitecture;
                existingDevice.OSDescription = device.OSDescription;
                existingDevice.Platform = device.Platform;
                existingDevice.ProcessorCount = device.ProcessorCount;
                existingDevice.TotalMemory = device.TotalMemory;
                existingDevice.TotalStorage = device.TotalStorage;
                existingDevice.AgentVersion = device.AgentVersion;
                updatedDevice = existingDevice;
            }
            else
            {

                if (HostEnvironment.IsDevelopment() && RemotelyContext.Organizations.Any())
                {
                    var org = RemotelyContext.Organizations.FirstOrDefault();
                    device.Organization = org;
                    device.OrganizationID = org?.ID;
                }

                updatedDevice = device;

                if (!RemotelyContext.Organizations.Any(x => x.ID == device.OrganizationID))
                {
                    WriteEvent(new EventLog()
                    {
                        EventType = EventType.Info,
                        Message = $"Unable to add device {device.DeviceName} because organization {device.OrganizationID} does not exist.",
                        Source = "DataService.AddOrUpdateDevice"
                    });
                    return false;
                }
                RemotelyContext.Devices.Add(device);
            }
            RemotelyContext.SaveChanges();
            return true;
        }

        public string AddSharedFile(IFormFile file, string organizationID)
        {
            var expirationDate = DateTime.Now.AddDays(-AppConfig.DataRetentionInDays);
            var expiredFiles = RemotelyContext.SharedFiles.Where(x => x.Timestamp < expirationDate);
            RemotelyContext.RemoveRange(expiredFiles);

            byte[] fileContents;
            using (var stream = file.OpenReadStream())
            {
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    fileContents = ms.ToArray();
                }
            }
            var newEntity = RemotelyContext.Add(new SharedFile()
            {
                FileContents = fileContents,
                FileName = file.FileName,
                ContentType = file.ContentType,
                OrganizationID = organizationID
            });
            RemotelyContext.SaveChanges();
            return newEntity.Entity.ID;
        }

        public void ChangeUserIsAdmin(string organizationID, string targetUserID, bool isAdmin)
        {
            var targetUser = RemotelyContext.Users.FirstOrDefault(x =>
                                x.OrganizationID == organizationID &&
                                x.Id == targetUserID);

            if (targetUser != null)
            {
                targetUser.IsAdministrator = isAdmin;
                RemotelyContext.SaveChanges();
            }
        }

        public void CleanupOldRecords()
        {
            if (AppConfig.DataRetentionInDays > 0)
            {

                var expirationDate = DateTime.Now - TimeSpan.FromDays(AppConfig.DataRetentionInDays);

                var eventLogs = RemotelyContext.EventLogs
                                    .Where(x => x.TimeStamp < expirationDate);

                RemotelyContext.RemoveRange(eventLogs);

                var commandContexts = RemotelyContext.CommandContexts
                                        .Where(x => x.TimeStamp < expirationDate);

                RemotelyContext.RemoveRange(commandContexts);

                var sharedFiles = RemotelyContext.SharedFiles
                                        .Where(x => x.Timestamp < expirationDate);

                RemotelyContext.RemoveRange(sharedFiles);

                RemotelyContext.SaveChanges();
            }
        }

        public async Task<ApiToken> CreateApiToken(string userName, string tokenName)
        {
            var user = RemotelyContext.Users.FirstOrDefault(x => x.UserName == userName);

            var newToken = new ApiToken()
            {
                Name = tokenName,
                OrganizationID = user.OrganizationID,
                Token = Guid.NewGuid().ToString(),
                Secret = PasswordGenerator.GeneratePassword(24)
            };
            RemotelyContext.ApiTokens.Add(newToken);
            await RemotelyContext.SaveChangesAsync();
            return newToken;
        }

        public async Task DeleteApiToken(string userName, string tokenId)
        {
            var user = RemotelyContext.Users.FirstOrDefault(x => x.UserName == userName);
            var token = RemotelyContext.ApiTokens.FirstOrDefault(x =>
                x.OrganizationID == user.OrganizationID &&
                x.ID == tokenId);

            RemotelyContext.ApiTokens.Remove(token);
            await RemotelyContext.SaveChangesAsync();
        }

        public void DeleteDeviceGroup(string orgID, string deviceGroupId)
        {
            var deviceGroup = RemotelyContext.DeviceGroups.FirstOrDefault(x =>
                x.ID == deviceGroupId &&
                x.OrganizationID == orgID);

            deviceGroup.Devices.ForEach(x =>
            {
                x.DeviceGroup = null;
            });

            RemotelyContext.DeviceGroups.Remove(deviceGroup);
            RemotelyContext.SaveChanges();
        }

        public void DeleteInvite(string orgID, string inviteID)
        {
            var invite = RemotelyContext.InviteLinks.FirstOrDefault(x =>
                x.OrganizationID == orgID &&
                x.ID == inviteID);

            var user = RemotelyContext.Users.FirstOrDefault(x => x.UserName == invite.InvitedUser);

            if (user != null && string.IsNullOrWhiteSpace(user.PasswordHash))
            {
                RemotelyContext.Remove(user);
            }
            RemotelyContext.Remove(invite);
            RemotelyContext.SaveChanges();
        }

        public void DetachEntity(object entity)
        {
            RemotelyContext.Entry(entity).State = EntityState.Detached;
        }

        public void DeviceDisconnected(string deviceID)
        {
            var device = RemotelyContext.Devices.Find(deviceID);
            if (device != null)
            {
                device.LastOnline = DateTime.Now;
                device.IsOnline = false;
                RemotelyContext.SaveChanges();
            }
        }

        public bool DoesUserExist(string userName)
        {
            if (userName == null)
            {
                return false;
            }
            return RemotelyContext.Users.Any(x => x.UserName == userName);
        }

        public bool DoesUserHaveAccessToDevice(string deviceID, RemotelyUser remotelyUser)
        {
            return RemotelyContext.Devices.Any(x => x.ID == deviceID && x.OrganizationID == remotelyUser.OrganizationID);
        }

        public bool DoesUserHaveAccessToDevice(string deviceID, string remotelyUserID)
        {
            var remotelyUser = RemotelyContext.Users.Find(remotelyUserID);

            return RemotelyContext.Devices.Any(x => x.ID == deviceID && x.OrganizationID == remotelyUser.OrganizationID);
        }

        public string[] FilterDeviceIDsByUserPermission(string[] deviceIDs, RemotelyUser remotelyUser)
        {
            return RemotelyContext.Devices.Where(x =>
                        deviceIDs.Contains(x.ID) &&
                        x.OrganizationID == remotelyUser.OrganizationID
                    )
                    .Select(x => x.ID)
                    .ToArray();
        }

        public IEnumerable<ApiToken> GetAllApiTokens(string userID)
        {
            var user = RemotelyContext.Users.FirstOrDefault(x => x.Id == userID);

            return RemotelyContext.ApiTokens
                .Where(x => x.OrganizationID == user.OrganizationID)
                .OrderByDescending(x => x.LastUsed);
        }

        public IEnumerable<CommandContext> GetAllCommandContexts(string orgID)
        {
            return RemotelyContext.CommandContexts
                .Where(x => x.OrganizationID == orgID)
                .OrderByDescending(x => x.TimeStamp);
        }

        public IEnumerable<Device> GetAllDevices(string orgID)
        {
            return RemotelyContext.Devices.Where(x => x.OrganizationID == orgID);
        }

        public IEnumerable<EventLog> GetAllEventLogs(string orgID)
        {
            return RemotelyContext.EventLogs
                .Where(x => x.OrganizationID == orgID)
                .OrderByDescending(x => x.TimeStamp);
        }

        public ICollection<InviteLink> GetAllInviteLinks(string userName)
        {
            return RemotelyContext.Users
                   .Include(x => x.Organization)
                   .ThenInclude(x => x.InviteLinks)
                   .FirstOrDefault(x => x.UserName == userName)
                   .Organization
                   .InviteLinks;
        }

        public IEnumerable<RemotelyUser> GetAllUsers(string userName)
        {
            var user = RemotelyContext.Users.FirstOrDefault(x => x.UserName == userName);
            return RemotelyContext.Users.Where(x => x.OrganizationID == user.OrganizationID);
        }

        public ApiToken GetApiToken(string apiToken)
        {
            return RemotelyContext.ApiTokens.FirstOrDefault(x => x.Token == apiToken);
        }

        public CommandContext GetCommandContext(string commandContextID, string orgID)
        {
            return RemotelyContext.CommandContexts
                .FirstOrDefault(x =>
                    x.OrganizationID == orgID &&
                    x.ID == commandContextID);
        }

        public CommandContext GetCommandContext(string commandContextID)
        {
            return RemotelyContext.CommandContexts.Find(commandContextID);
        }

        public string GetDefaultPrompt(string userName)
        {
            var userPrompt = RemotelyContext.Users.FirstOrDefault(x => x.UserName == userName)?.UserOptions?.ConsolePrompt;
            return userPrompt ?? AppConfig.DefaultPrompt;
        }

        public string GetDefaultPrompt()
        {
            return AppConfig.DefaultPrompt;
        }

        public Device GetDevice(string orgID, string deviceID)
        {
            return RemotelyContext.Devices.FirstOrDefault(x =>
                            x.OrganizationID == orgID &&
                            x.ID == deviceID);
        }

        public int GetDeviceCount()
        {
            return RemotelyContext.Devices.Count();
        }

        public IEnumerable<DeviceGroup> GetDeviceGroupsForUserName(string username)
        {
            var user = RemotelyContext.Users.FirstOrDefault(x => x.UserName == username);

            return RemotelyContext.DeviceGroups.Where(x => x.OrganizationID == user.OrganizationID) ?? Enumerable.Empty<DeviceGroup>();
        }

        public IEnumerable<Device> GetDevicesForUser(string userName)
        {
            // TODO: Add authorization groups.
            var user = RemotelyContext.Users.FirstOrDefault(x => x.UserName == userName);
            return RemotelyContext.Devices.Where(x => x.OrganizationID == user.OrganizationID);
        }

        public IEnumerable<EventLog> GetEventLogs(string userName, DateTime from, DateTime to)
        {
            var orgID = RemotelyContext.Users
                        .FirstOrDefault(x => x.UserName == userName)
                        ?.OrganizationID;

            return RemotelyContext.EventLogs
                .Where(x => x.OrganizationID == orgID && x.TimeStamp >= from && x.TimeStamp <= to)
                .OrderByDescending(x => x.TimeStamp);
        }

        public int GetOrganizationCount()
        {
            return RemotelyContext.Organizations.Count();
        }

        public string GetOrganizationName(string userName)
        {
            return RemotelyContext.Users
                   .Include(x => x.Organization)
                   .FirstOrDefault(x => x.UserName == userName)
                   .Organization
                   .OrganizationName;
        }

        public SharedFile GetSharedFiled(string fileID)
        {
            return RemotelyContext.SharedFiles.Find(fileID);
        }

        public RemotelyUser GetUserByID(string userID)
        {
            if (userID == null)
            {
                return null;
            }
            return RemotelyContext.Users.FirstOrDefault(x => x.Id == userID);
        }

        public RemotelyUser GetUserByName(string userName)
        {
            if (userName == null)
            {
                return null;
            }
            return RemotelyContext.Users
                .Include(x => x.Organization)
                .FirstOrDefault(x => x.UserName == userName);
        }

        public RemotelyUserOptions GetUserOptions(string userName)
        {
            return RemotelyContext.Users
                    .FirstOrDefault(x => x.UserName == userName)
                    .UserOptions;
        }

        public bool JoinViaInvitation(string userName, string inviteID)
        {
            var invite = RemotelyContext.InviteLinks.FirstOrDefault(x =>
                            x.InvitedUser.ToLower() == userName.ToLower() &&
                            x.ID == inviteID);

            if (invite == null)
            {
                return false;
            }

            var user = RemotelyContext.Users.FirstOrDefault(x => x.UserName == userName);
            var organization = RemotelyContext.Organizations
                                .Include(x => x.RemotelyUsers)
                                .FirstOrDefault(x => x.ID == invite.OrganizationID);

            user.Organization = organization;
            user.OrganizationID = organization.ID;
            user.IsAdministrator = invite.IsAdmin;
            organization.RemotelyUsers.Add(user);

            RemotelyContext.SaveChanges();

            RemotelyContext.InviteLinks.Remove(invite);
            RemotelyContext.SaveChanges();
            return true;
        }

        public void RemoveDevices(string[] deviceIDs)
        {
            var devices = RemotelyContext.Devices
                .Where(x => deviceIDs.Contains(x.ID));

            RemotelyContext.Devices.RemoveRange(devices);
            RemotelyContext.SaveChanges();
        }

        public void RemoveUserFromOrganization(string orgID, string targetUserID)
        {
            var target = RemotelyContext.Users.FirstOrDefault(x =>
                x.Id == targetUserID &&
                x.OrganizationID == orgID);

            var newOrganization = new Organization();
            target.Organization = newOrganization;
            RemotelyContext.Organizations.Add(newOrganization);
            RemotelyContext.SaveChanges();
        }

        public async Task RenameApiToken(string userName, string tokenId, string tokenName)
        {
            var user = RemotelyContext.Users.FirstOrDefault(x => x.UserName == userName);
            var token = RemotelyContext.ApiTokens.FirstOrDefault(x =>
                x.OrganizationID == user.OrganizationID &&
                x.ID == tokenId);

            token.Name = tokenName;
            await RemotelyContext.SaveChangesAsync();
        }

        public void SetAllDevicesNotOnline()
        {
            RemotelyContext.Devices.ForEachAsync(x =>
            {
                x.IsOnline = false;
            }).Wait();
            RemotelyContext.SaveChanges();
        }

        public void SetDeviceSetupOptions(string deviceID, DeviceSetupOptions options)
        {
            var device = RemotelyContext.Devices.FirstOrDefault(x => x.ID == deviceID);
            if (device != null)
            {
                if (!string.IsNullOrWhiteSpace(options.DeviceAlias))
                {
                    device.Alias = options.DeviceAlias;
                }

                if (!string.IsNullOrWhiteSpace(options.DeviceGroup))
                {
                    var group = RemotelyContext.DeviceGroups.FirstOrDefault(x =>
                    x.Name.ToLower() == options.DeviceGroup.ToLower() &&
                    x.OrganizationID == device.OrganizationID);
                    device.DeviceGroup = group;
                }

                RemotelyContext.SaveChanges();
            }
        }

        public void SetServerVerificationToken(string deviceID, string verificationToken)
        {
            var device = RemotelyContext.Devices.Find(deviceID);
            if (device != null)
            {
                device.ServerVerificationToken = verificationToken;
                RemotelyContext.SaveChanges();
            }
        }

        public void UpdateDevice(string deviceID, string tag, string alias, string deviceGroupID)
        {
            var device = RemotelyContext.Devices.Find(deviceID);
            if (device == null)
            {
                return;
            }

            device.Tags = tag;
            device.DeviceGroupID = deviceGroupID;
            device.Alias = alias;
            RemotelyContext.SaveChanges();
        }

        public void UpdateOrganizationName(string orgID, string organizationName)
        {
            RemotelyContext.Organizations
                .FirstOrDefault(x => x.ID == orgID)
                .OrganizationName = organizationName;
            RemotelyContext.SaveChanges();
        }

        public void UpdateTags(string deviceID, string tags)
        {
            var device = RemotelyContext.Devices.Find(deviceID);
            if (device == null)
            {
                return;
            }

            device.Tags = tags;
            RemotelyContext.SaveChanges();
        }

        public void UpdateUserOptions(string userName, RemotelyUserOptions options)
        {
            RemotelyContext.Users.FirstOrDefault(x => x.UserName == userName).UserOptions = options;
            RemotelyContext.SaveChanges();
        }

        public bool ValidateApiToken(string apiToken, string apiSecret, string requestPath, string remoteIP)
        {
            var token = RemotelyContext.ApiTokens.FirstOrDefault(x => x.Token == apiToken);
            var isValid = token != null && token.Secret == apiSecret;

            if (token != null)
            {
                token.LastUsed = DateTime.Now;
                RemotelyContext.SaveChanges();
            }

            WriteEvent($"API token used.  Token: {apiToken}.  Path: {requestPath}.  Validated: {isValid}.  Remote IP: {remoteIP}", EventType.Info, token?.OrganizationID);

            return isValid;
        }
        public void WriteEvent(EventLog eventLog)
        {
            RemotelyContext.EventLogs.Add(eventLog);
            RemotelyContext.SaveChanges();
        }

        public void WriteEvent(Exception ex)
        {
            RemotelyContext.EventLogs.Add(new EventLog()
            {
                EventType = EventType.Error,
                Message = ex.Message,
                Source = ex.Source,
                StackTrace = ex.StackTrace,
                TimeStamp = DateTime.Now
            });
            RemotelyContext.SaveChanges();
        }

        public void WriteEvent(string message, string organizationId)
        {
            RemotelyContext.EventLogs.Add(new EventLog()
            {
                EventType = EventType.Info,
                Message = message,
                TimeStamp = DateTime.Now,
                OrganizationID = organizationId
            });
            RemotelyContext.SaveChanges();
        }

        public void WriteEvent(string message, EventType eventType, string organizationId)
        {
            RemotelyContext.EventLogs.Add(new EventLog()
            {
                EventType = eventType,
                Message = message,
                TimeStamp = DateTime.Now,
                OrganizationID = organizationId
            });
            RemotelyContext.SaveChanges();
        }
    }
}
