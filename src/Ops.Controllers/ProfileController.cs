﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Ocuda.Ops.Controllers.Abstract;
using Ocuda.Ops.Controllers.ViewModels.Profile;
using Ocuda.Ops.Models.Keys;
using Ocuda.Ops.Service.Interfaces.Ops.Services;
using Ocuda.Utility.Exceptions;
using Ocuda.Utility.Keys;

namespace Ocuda.Ops.Controllers
{
    [Route("[controller]")]
    public class ProfileController : BaseController<ProfileController>
    {
        private readonly IHttpContextAccessor _httpContext;
        private readonly ILocationService _locationService;
        private readonly IPermissionGroupService _permissionGroupService;
        private readonly IUserService _userService;

        public ProfileController(ServiceFacades.Controller<ProfileController> context,
            IHttpContextAccessor httpContext,
            ILocationService locationService,
            IPermissionGroupService permissionGroupService,
            IUserService userService) : base(context)
        {
            _httpContext = httpContext ?? throw new ArgumentNullException(nameof(httpContext));
            _locationService = locationService
                ?? throw new ArgumentNullException(nameof(locationService));
            _permissionGroupService = permissionGroupService
                ?? throw new ArgumentNullException(nameof(permissionGroupService));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
        }

        public static string Name
        { get { return "Profile"; } }

        [HttpPost("[action]")]
        public async Task<IActionResult> EditNickname(IndexViewModel model)
        {
            if (model?.User.Id != CurrentUserId)
            {
                return RedirectToUnauthorized();
            }
            if (ModelState.IsValid && model != null)
            {
                try
                {
                    var user = await _userService.EditNicknameAsync(model.User);
                    ShowAlertSuccess($"Updated nickname: {user.Nickname}");
                }
                catch (OcudaException oex)
                {
                    ShowAlertDanger("Unable to update nickname: ", oex.Message);
                }
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpGet("")]
        [HttpGet("{id}")]
        public async Task<IActionResult> Index(string id)
        {
            var viewModel = new IndexViewModel
            {
                CanUpdatePicture = IsSiteManager(),
                CanViewLastSeen = IsSiteManager(),
                Locations = await GetLocationsDropdownAsync(_locationService),
                UserViewingSelf = string.IsNullOrEmpty(id)
                    || id == UserClaim(ClaimType.Username)
            };

            if (!viewModel.CanUpdatePicture)
            {
                viewModel.CanUpdatePicture = await HasAppPermissionAsync(_permissionGroupService,
                    ApplicationPermission.UpdateProfilePictures);
            }

            if (!viewModel.UserViewingSelf)
            {
                var user = await _userService.LookupUserAsync(id);

                if (user != null)
                {
                    viewModel.User = user;
                }
                else
                {
                    ShowAlertDanger("Could not find user with username: ", id);
                    return RedirectToAction(nameof(Index), HomeController.Name);
                }
            }
            else
            {
                viewModel.User = await _userService.GetByIdAsync(CurrentUserId);
            }

            if (!string.IsNullOrEmpty(viewModel.User.PictureFilename))
            {
                viewModel.PicturePath = Url.Action(nameof(Picture),
                    new { id = id ?? UserClaim(ClaimType.Username) });
            }

            if (viewModel.User.SupervisorId.HasValue)
            {
                viewModel.User.Supervisor =
                    await _userService.GetByIdAsync(viewModel.User.SupervisorId.Value);
            }

            viewModel.DirectReports = await _userService.GetDirectReportsAsync(viewModel.User.Id);

            viewModel.CanEdit = viewModel.User.Id == CurrentUserId;

            if (viewModel.UserViewingSelf)
            {
                viewModel.AuthenticatedAt = DateTime.Parse(UserClaim(ClaimType.AuthenticatedAt),
                    CultureInfo.InvariantCulture);

                viewModel.Permissions = new List<string>();

                if (!string.IsNullOrEmpty(UserClaim(ClaimType.SiteManager)))
                {
                    viewModel.Permissions.Add("Site manager");
                }

                var permissionClaims = UserClaims(ClaimType.PermissionId);

                if (permissionClaims?.Count > 0)
                {
                    var permissionGroupIds = permissionClaims
                        .Select(_ => int.Parse(_, CultureInfo.InvariantCulture));

                    var permissionLookup = await _permissionGroupService
                        .GetGroupsAsync(permissionGroupIds);

                    var permissionGroups = permissionLookup
                            .Select(_ => _.PermissionGroupName)
                            .OrderBy(_ => _);

                    viewModel.Permissions = viewModel.Permissions
                        .Concat(permissionGroups)
                        .ToList();
                }
            }

            viewModel.RelatedTitleClassifications
                = await _userService.GetRelatedTitleClassificationsAsync(viewModel.User.Id);

            return View(viewModel);
        }

        [HttpGet("[action]/{id}")]
        public async Task<IActionResult> Picture(string id)
        {
            var picture = await _userService.GetProfilePictureAsync(id);

            if (picture == null)
            {
                return StatusCode(StatusCodes.Status404NotFound);
            }

            Response.Headers.Add("Content-Disposition", "inline; filename=" + picture.Filename);
            return File(picture.FileData, picture.FileType);
        }

        [HttpPost("[action]")]
        public async Task<IActionResult> Reauthenticate()
        {
            await _httpContext.HttpContext.SignOutAsync();
            return RedirectToAction(nameof(Index));
        }

        [HttpPost("[action]")]
        public async Task<IActionResult> RemovePicture(int userId, string username)
        {
            if (!IsSiteManager())
            {
                var picturePermission = await HasAppPermissionAsync(_permissionGroupService,
                    ApplicationPermission.UpdateProfilePictures);
                if (!picturePermission)
                {
                    return RedirectToUnauthorized();
                }
            }

            await _userService.RemoveProfilePictureAsync(userId);
            return RedirectToAction(nameof(Index), new { id = username });
        }

        [HttpPost("[action]")]
        public async Task<IActionResult> UnsetManualLocation(int userId)
        {
            if (userId != CurrentUserId)
            {
                return RedirectToUnauthorized();
            }

            await _userService.UnsetManualLocationAsync(userId);

            return RedirectToAction(nameof(Index));
        }

        [HttpPost("[action]")]
        public async Task<IActionResult> UpdateLocation(int userId, int locationId)
        {
            if (userId != CurrentUserId)
            {
                return RedirectToUnauthorized();
            }
            await _userService.UpdateLocationAsync(userId, locationId);
            return RedirectToAction(nameof(Index));
        }

        [HttpGet("[action]/{id}")]
        public async Task<IActionResult> UpdatePicture(int id)
        {
            if (!IsSiteManager())
            {
                var picturePermission = await HasAppPermissionAsync(_permissionGroupService,
                    ApplicationPermission.UpdateProfilePictures);
                if (!picturePermission)
                {
                    return RedirectToUnauthorized();
                }
            }

            var user = await _userService.GetByIdAsync(id);
            if (user == null)
            {
                ShowAlertDanger("Unable to find that user.");
                return RedirectToAction(nameof(Index));
            }

            return View(new UpdatePictureViewModel
            {
                CropHeight = 700,
                CropWidth = 700,
                DisplayDimension = 700,
                User = user
            });
        }

        [HttpPost("[action]")]
        public async Task<IActionResult>
            UploadPicture(UpdatePictureViewModel updatePictureViewModel)
        {
            if (!IsSiteManager())
            {
                var picturePermission = await HasAppPermissionAsync(_permissionGroupService,
                    ApplicationPermission.UpdateProfilePictures);
                if (!picturePermission)
                {
                    return RedirectToUnauthorized();
                }
            }

            if (updatePictureViewModel == null)
            {
                return RedirectToAction(nameof(Index));
            }

            var user = await _userService.GetByIdAsync(updatePictureViewModel.UserId);

            if (user == null)
            {
                ShowAlertDanger("Unable to find that user.");
                return RedirectToAction(nameof(Index));
            }

            if (string.IsNullOrEmpty(updatePictureViewModel.ProfilePicture))
            {
                ShowAlertWarning("You must upload a file to replace a profile image.");
                return RedirectToAction(nameof(Index), new { id = user.Username });
            }

            try
            {
                await _userService
                    .UploadProfilePictureAsync(user, updatePictureViewModel.ProfilePicture);
            }
            catch (OcudaException oex)
            {
                ShowAlertDanger("Problem with upload: " + oex.Message);
            }

            return RedirectToAction(nameof(Index), new { id = user.Username });
        }
    }
}