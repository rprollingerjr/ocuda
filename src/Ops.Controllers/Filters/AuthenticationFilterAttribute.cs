﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Ocuda.Ops.Service.Interfaces.Ops.Services;
using Ocuda.Utility.Abstract;
using Ocuda.Utility.Helpers;
using Ocuda.Utility.Keys;
using Ocuda.Utility.Services.Interfaces;
using Serilog.Context;

namespace Ocuda.Ops.Controllers.Filters
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = false)]
    public sealed class AuthenticationFilterAttribute : Attribute, IAsyncResourceFilter
    {
        private readonly IAuthorizationService _authorizationService;
        private readonly IOcudaCache _cache;
        private readonly IConfiguration _config;
        private readonly IDateTimeProvider _dateTimeProvider;
        private readonly ILdapService _ldapService;
        private readonly ILogger<AuthenticationFilterAttribute> _logger;
        private readonly IUserService _userService;
        private readonly WebHelper _webHelper;

        public AuthenticationFilterAttribute(ILogger<AuthenticationFilterAttribute> logger,
            IAuthorizationService authorizationService,
            IConfiguration configuration,
            IDateTimeProvider dateTimeProvider,
            ILdapService ldapService,
            IOcudaCache cache,
            IUserService userService,
            WebHelper webHelper)
        {
            _authorizationService = authorizationService
                ?? throw new ArgumentNullException(nameof(authorizationService));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _config = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _dateTimeProvider = dateTimeProvider
                ?? throw new ArgumentNullException(nameof(dateTimeProvider));
            _ldapService = ldapService ?? throw new ArgumentNullException(nameof(ldapService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _webHelper = webHelper ?? throw new ArgumentNullException(nameof(webHelper));
        }

        public async Task OnResourceExecutionAsync(ResourceExecutingContext context,
            ResourceExecutionDelegate next)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (next == null)
            {
                throw new ArgumentNullException(nameof(next));
            }

            string username = null;
            string userId = null;

            var now = _dateTimeProvider.Now;
            var authRedirectUrl = _config[Configuration.OpsAuthRedirect];

            if (!string.IsNullOrEmpty(authRedirectUrl))
            {
                // check the current user's Username claim to see if they're authenticated
                var usernameClaim = context.HttpContext.User.Claims
                    .FirstOrDefault(_ => _.Type == ClaimType.Username);

                bool authenticateUser = usernameClaim == null;

                if (!authenticateUser)
                {
                    // user is logged in, ensure they exist in the database
                    // TODO probably figure out how to make this use the database less
                    username = usernameClaim.Value;
                    var user = await _userService.LookupUserAsync(username);
                    if (user?.ReauthenticateUser != false)
                    {
                        // user does not exist in the database or needs to be reauthenticated
                        authenticateUser = true;
                        await context.HttpContext.SignOutAsync();
                    }
                }

                if (authenticateUser)
                {
                    // by default time out cookies and distributed cache in 2 minutes
                    int authTimeoutMinutes = 2;

                    var configuredAuthTimeout = _config[Configuration.OpsAuthTimeoutMinutes];
                    if (configuredAuthTimeout != null
                        && !int.TryParse(configuredAuthTimeout, out authTimeoutMinutes))
                    {
                        _logger.LogWarning("Configured {OpsAuthTimeoutMinutes} could not be converted to a number. It should be a number of minutes (defaulting to 2).",
                            Configuration.OpsAuthTimeoutMinutes);
                    }

                    // all authentication bits will expire after 2 minutes
                    var authenticationExpiration = new TimeSpan(0, authTimeoutMinutes, 0);

                    // check existing authentication id cookie
                    var id = context.HttpContext.Request.Cookies[Cookie.OpsAuthId];
                    if (id == null)
                    {
                        // create a new authentication id cookie if none exists
                        id = Guid.NewGuid().ToString();
                        context.HttpContext.Response.Cookies.Append(Cookie.OpsAuthId,
                            id,
                            new Microsoft.AspNetCore.Http.CookieOptions
                            {
                                IsEssential = true,
                                MaxAge = authenticationExpiration
                            }
                        );
                    }

                    // check if there's a username stored in the cache
                    // if so, the authentication has redirected us back here
                    username = await _cache.GetStringFromCache(string.Format(CultureInfo.InvariantCulture,
                            Cache.OpsUsername,
                            id));

                    if (string.IsNullOrEmpty(username))
                    {
                        // if there is no username: set a return url and redirect to authentication
                        await _cache.SaveToCacheAsync(string
                                .Format(CultureInfo.InvariantCulture, Cache.OpsReturn, id),
                            _webHelper.GetCurrentUrl(context.HttpContext),
                            authenticationExpiration);

                        string cacheDiscriminator
                            = _config[Configuration.OpsDistributedCacheInstanceDiscriminator]
                            ?? string.Empty;

                        var url = string.Format(CultureInfo.InvariantCulture,
                            authRedirectUrl,
                            id,
                            cacheDiscriminator);

                        context.HttpContext.Response.Redirect(url);
                        return;
                    }
                    else
                    {
                        // remove the username from the cache
                        await _cache.RemoveAsync(string.Format(CultureInfo.InvariantCulture,
                            Cache.OpsUsername,
                            id));

                        // check if there's a domain name specified and strip it from the username
                        var domainName = _config[Configuration.OpsDomainName];
                        if (!string.IsNullOrEmpty(domainName)
                            && username.StartsWith(domainName, StringComparison.OrdinalIgnoreCase))
                        {
                            username = username[(domainName.Length + 1)..];
                        }

                        var user = await _userService.LookupUserAsync(username);
                        var newUser = user == null;
                        if (newUser)
                        {
                            user = new Models.Entities.User
                            {
                                Username = username,
                                LastSeen = now
                            };
                        }

                        // perform ldap update of user object
                        user = _ldapService.LookupByUsername(user);

                        // if the user is new, add them to the database
                        if (newUser)
                        {
                            var rosterUser = await _userService.LookupUserByEmailAsync(user.Email);
                            if (rosterUser != null)
                            {
                                _logger.LogInformation("New user {Username} detected, found in database with email {Email}",
                                    user.Username,
                                    user.Email);
                                user = await _userService
                                    .UpdateRosterUserAsync(rosterUser.Id, user);
                            }
                            else
                            {
                                _logger.LogInformation("New user {Username} detected, inserting into database", user.Username);
                                user = await _userService.AddUser(user);
                            }
                        }
                        else
                        {
                            await _userService.LoggedInUpdateAsync(user);
                        }

                        userId = user.Id.ToString(CultureInfo.InvariantCulture);

                        // start creating the user's claims with their username
                        var claims = new HashSet<Claim>
                        {
                            new Claim(ClaimType.Username, username),
                            new Claim(ClaimType.UserId, userId),
                            new Claim(ClaimType.AuthenticatedAt, now
                                .ToString("O", CultureInfo.InvariantCulture))
                        };

                        // loop through groups in the distributed cache from authentication
                        // prime the loop
                        int groupId = 1;
                        var adGroupName
                            = await _cache.GetStringFromCache(string
                                .Format(CultureInfo.InvariantCulture,
                                    Cache.OpsGroup,
                                    id,
                                    groupId));
                        var adGroupNames = new List<string>();
                        while (!string.IsNullOrEmpty(adGroupName))
                        {
                            adGroupNames.Add(adGroupName);
                            // once it's in our list, remove it from the cache
                            await _cache.RemoveAsync(string.Format(CultureInfo.InvariantCulture,
                                Cache.OpsGroup,
                                id,
                                groupId));
                            groupId++;
                            adGroupName = await _cache
                                .GetStringFromCache(string.Format(CultureInfo.InvariantCulture,
                                    Cache.OpsGroup,
                                    id,
                                    groupId));
                        }

                        bool isSiteManager = false;

                        // pull lists of AD groups that should be site managers
                        var claimGroups = await _authorizationService.GetClaimGroupsAsync();
                        var permissionGroups
                            = await _authorizationService.GetPermissionGroupsAsync();

                        var claimantOf = new Dictionary<string, string>();
                        var inPermissionGroup = new List<int>();

                        // loop through group names and look up if each group provides claims
                        // claims can be provided via ClaimGroups
                        foreach (string groupName in adGroupNames)
                        {
                            claims.Add(new Claim(ClaimType.ADGroup, groupName));

                            // once the user is a site manager, we can stop looking up more rights
                            if (!isSiteManager)
                            {
                                foreach (var claim in claimGroups
                                    .Where(_ => _.GroupName == groupName))
                                {
                                    if (!claimantOf.ContainsKey(claim.ClaimType))
                                    {
                                        claimantOf.Add(claim.ClaimType, groupName);
                                    }
                                }

                                var permissionList = permissionGroups
                                    .Where(_ => _.GroupName == groupName);
                                foreach (var permission in permissionList)
                                {
                                    inPermissionGroup.Add(permission.Id);
                                }

                                if (claimantOf.ContainsKey(ClaimType.SiteManager))
                                {
                                    isSiteManager = true;
                                }
                            }
                        }

                        if (isSiteManager)
                        {
                            // also add each individual permission claim
                            foreach (var claimType in claimGroups)
                            {
                                claims.Add(new Claim(claimType.ClaimType,
                                    ClaimType.SiteManager));
                            }

                            foreach (var permissionId in permissionGroups.Select(_ => _.Id))
                            {
                                claims.Add(new Claim(ClaimType.PermissionId,
                                    permissionId.ToString(CultureInfo.InvariantCulture)));
                            }

                            claims.Add(new Claim(ClaimType.HasContentAdminRights,
                                ClaimType.HasContentAdminRights));
                            claims.Add(new Claim(ClaimType.HasSiteAdminRights,
                                ClaimType.HasSiteAdminRights));
                        }
                        else
                        {
                            // add permission claims
                            foreach (var claim in claimantOf)
                            {
                                claims.Add(new Claim(claim.Key, claim.Value));
                            }

                            foreach (var permissionId in inPermissionGroup)
                            {
                                claims.Add(new Claim(ClaimType.PermissionId,
                                    permissionId.ToString(CultureInfo.InvariantCulture)));
                            }

                            var hasAdminClaims = await _authorizationService
                                .GetAdminClaimsAsync(inPermissionGroup);

                            foreach (var hasAdminClaim in hasAdminClaims)
                            {
                                claims.Add(new Claim(hasAdminClaim, hasAdminClaim));
                            }
                        }

                        // TODO: probably change the role claim type to our roles and not AD groups
                        var identity = new ClaimsIdentity(claims,
                            CookieAuthenticationDefaults.AuthenticationScheme,
                            ClaimType.Username,
                            ClaimType.ADGroup);

                        await context.HttpContext.SignInAsync(new ClaimsPrincipal(identity));

                        // remove the return URL from the cache
                        await _cache.RemoveAsync(string.Format(CultureInfo.InvariantCulture,
                            Cache.OpsReturn,
                            id));

                        context.HttpContext.Response.Cookies.Delete(Cookie.OpsAuthId);

                        // TODO set a reasonable initial nickname
                        context.HttpContext.Items[ItemKey.Nickname] = user.Nickname ?? username;

                        // Set the users claims for this request
                        ((ClaimsIdentity)context.HttpContext.User.Identity).AddClaims(claims);
                    }
                }
            }

            using (LogContext.PushProperty(Utility.Logging.Enrichment.RouteAction,
                context.RouteData?.Values["action"]))
            using (LogContext.PushProperty(Utility.Logging.Enrichment.RouteArea,
                context.RouteData?.Values["area"]))
            using (LogContext.PushProperty(Utility.Logging.Enrichment.RouteController,
                context.RouteData?.Values["controller"]))
            using (LogContext.PushProperty(Utility.Logging.Enrichment.RouteId,
                context.RouteData?.Values["id"]))
            using (LogContext.PushProperty(Utility.Logging.Enrichment.UserId, userId))
            using (LogContext.PushProperty(Utility.Logging.Enrichment.Username, username))
            {
                await next();
            }
        }
    }
}