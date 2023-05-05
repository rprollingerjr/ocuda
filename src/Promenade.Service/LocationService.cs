﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BranchLocator.Helpers;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Ocuda.Promenade.Models;
using Ocuda.Promenade.Models.Entities;
using Ocuda.Promenade.Service.Abstract;
using Ocuda.Promenade.Service.Interfaces.Repositories;
using Ocuda.Utility.Abstract;
using Ocuda.Utility.Services.Interfaces;

namespace Ocuda.Promenade.Service
{
    public class LocationService : BaseService<LocationService>
    {
        private const int CacheLocationFeatureHours = 1;
        private const int CacheLocationGroupHours = 1;
        private const int CacheLocationHours = 1;
        private const int CacheLocationScheduleHours = 1;
        private const int CacheSlugIdMapHours = 12;
        private const int DaysInWeek = 7;
        private const string ndash = "\u2013";

        private readonly IOcudaCache _cache;
        private readonly IFeatureRepository _featureRepository;
        private readonly IGoogleClient _googleClient;
        private readonly IGroupRepository _groupRepository;
        private readonly IStringLocalizer<i18n.Resources.Shared> _localizer;
        private readonly ILocationFeatureRepository _locationFeatureRepository;
        private readonly ILocationGroupRepository _locationGroupRepository;
        private readonly ILocationHoursOverrideRepository _locationHoursOverrideRepository;
        private readonly ILocationHoursRepository _locationHoursRepository;
        private readonly ILocationRepository _locationRepository;
        private readonly SegmentService _segmentService;

        public LocationService(ILogger<LocationService> logger,
            IDateTimeProvider dateTimeProvider,
            IStringLocalizer<i18n.Resources.Shared> localizer,
            IFeatureRepository featureRepository,
            IGoogleClient googleClient,
            IGroupRepository groupRepository,
            ILocationFeatureRepository locationFeatureRepository,
            ILocationGroupRepository locationGroupRepository,
            ILocationHoursOverrideRepository locationHoursOverrideRepository,
            ILocationHoursRepository locationHoursRepository,
            ILocationRepository locationRepository,
            IOcudaCache cache,
            SegmentService segmentService)
            : base(logger, dateTimeProvider)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _featureRepository = featureRepository
                ?? throw new ArgumentNullException(nameof(featureRepository));
            _googleClient = googleClient ?? throw new ArgumentNullException(nameof(googleClient));
            _groupRepository = groupRepository
                ?? throw new ArgumentNullException(nameof(groupRepository));
            _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
            _locationFeatureRepository = locationFeatureRepository
                ?? throw new ArgumentNullException(nameof(locationFeatureRepository));
            _locationGroupRepository = locationGroupRepository
                ?? throw new ArgumentNullException(nameof(locationGroupRepository));
            _locationHoursOverrideRepository = locationHoursOverrideRepository
                ?? throw new ArgumentNullException(nameof(locationHoursOverrideRepository));
            _locationHoursRepository = locationHoursRepository
                ?? throw new ArgumentNullException(nameof(locationHoursRepository));
            _locationRepository = locationRepository
                ?? throw new ArgumentNullException(nameof(locationRepository));
            _segmentService = segmentService
                ?? throw new ArgumentNullException(nameof(segmentService));
        }

        public async Task<(double? Latitude, double? Longitude)> GeocodeAddressAsync(string address)
        {
            return await _googleClient.GeocodeAsync(address);
        }

        public async Task<List<Location>> GetAllLocationsAsync()
        {
            return await _locationRepository.GetAllLocations();
        }

        public async Task<string> GetClosureInformationAsync(DateTime date)
        {
            return await _locationHoursOverrideRepository.GetClosureInformationAsync(date);
        }

        public async Task<LocationHoursResult> GetCurrentStatusAsync(int locationId)
        {
            var location = await _locationRepository.FindAsync(locationId);

            if (location.IsAlwaysOpen)
            {
                return new LocationHoursResult
                {
                    Open = true,
                    IsCurrentlyOpen = true,
                    StatusMessage = "Open"
                };
            }

            if (location.HoursSegmentId != null)
            {
                return new LocationHoursResult
                {
                    IsSpecialHours = true,
                    StatusMessage = "Special Hours"
                };
            }

            if (location.IsClosed)
            {
                return new LocationHoursResult
                {
                    Open = false,
                    IsCurrentlyOpen = false,
                    StatusMessage = "Closed"
                };
            }

            var now = _dateTimeProvider.Now;

            var result = new LocationHoursResult();

            var todayOverride = await _locationHoursOverrideRepository
                .GetByDateAsync(locationId, now);

            if (todayOverride != null)
            {
                result.Open = todayOverride.Open;
                result.OpenTime = todayOverride.OpenTime;
                result.CloseTime = todayOverride.CloseTime;
            }
            else
            {
                var todayHours = await _locationHoursRepository.GetByDayOfWeek(locationId, now);

                result.Open = todayHours.Open;
                result.OpenTime = todayHours.OpenTime;
                result.CloseTime = todayHours.CloseTime;
            }

            var showNextOpen = false;

            if (result.Open)
            {
                if (!result.OpenTime.HasValue || !result.CloseTime.HasValue)
                {
                    result.StatusMessage = _localizer[i18n.Keys.Promenade.LocationOpen];
                    result.IsCurrentlyOpen = true;
                }
                else if (result.OpenTime.Value.TimeOfDay > now.TimeOfDay)
                {
                    var opensAt = result.OpenTime.Value.ToString("t", CultureInfo.CurrentCulture);
                    result.StatusMessage = _localizer[i18n.Keys.Promenade.LocationOpensAtItem,
                        opensAt];
                    result.NextStatusChange = new DateTime(_dateTimeProvider.Now.Date.Ticks
                        + result.OpenTime.Value.TimeOfDay.Ticks);
                }
                else if (result.CloseTime.Value.TimeOfDay > now.TimeOfDay)
                {
                    var openUntil = result.CloseTime.Value.ToString("t",
                        CultureInfo.CurrentCulture);
                    result.StatusMessage = _localizer[i18n.Keys.Promenade.LocationOpenUntilItem,
                        openUntil];
                    result.IsCurrentlyOpen = true;
                    result.NextStatusChange = new DateTime(_dateTimeProvider.Now.Date.Ticks
                        + result.CloseTime.Value.TimeOfDay.Ticks);
                }
                else
                {
                    result.StatusMessage = _localizer[i18n.Keys.Promenade.LocationClosed];
                    showNextOpen = true;
                }
            }
            else
            {
                result.StatusMessage = _localizer[i18n.Keys.Promenade.LocationClosedToday];
                showNextOpen = true;
            }

            if (showNextOpen)
            {
                // get future overrides less than a week away for the location
                var futureOverrides = (await _locationHoursOverrideRepository
                    .GetBetweenDatesAsync(locationId, now.AddDays(1), now.AddDays(DaysInWeek - 1)))
                    .Select(_ => new LocationHours
                    {
                        Open = _.Open,
                        OpenTime = _.OpenTime,
                        DayOfWeek = _.Date.DayOfWeek
                    });

                // get branch hours for the week excluding the current day and days that have overrides
                var weeklyBranchHours = await _locationHoursRepository
                    .GetWeeklyHoursAsync(locationId);
                weeklyBranchHours = weeklyBranchHours
                    .Where(_ => _.DayOfWeek != now.DayOfWeek
                        && !futureOverrides.Select(d => d.DayOfWeek).Contains(_.DayOfWeek))
                    .ToList();

                // combine the lists and order by the next upcoming day to get the next branch opening
                var nextOpen = futureOverrides
                    .Concat(weeklyBranchHours)
                    .Where(_ => _.Open)
                    .OrderBy(_ => (_.DayOfWeek - now.DayOfWeek + DaysInWeek) % DaysInWeek)
                    .FirstOrDefault();

                if (nextOpen != null)
                {
                    var nextDay = "";
                    if ((int)nextOpen.DayOfWeek == ((int)now.DayOfWeek + 1) % DaysInWeek)
                    {
                        nextDay = _localizer[i18n.Keys.Promenade.LocationTomorrow];
                    }
                    else
                    {
                        nextDay = CultureInfo
                            .CurrentCulture
                            .DateTimeFormat
                            .GetAbbreviatedDayName(nextOpen.DayOfWeek);
                    }

                    var opensAt = nextOpen.OpenTime.Value.ToString("t",
                        CultureInfo.CurrentCulture);

                    result.StatusMessage = _localizer[i18n.Keys.Promenade.LocationOpensNextItem,
                        nextDay,
                        opensAt];

                    var nextOpenDayOfWeekDelta = ((int)nextOpen.DayOfWeek
                        - (int)_dateTimeProvider.Now.DayOfWeek + DaysInWeek) % DaysInWeek;

                    var nextOpenDay = _dateTimeProvider.Now.Date.AddDays(nextOpenDayOfWeekDelta);

                    result.NextStatusChange = nextOpenDay.Date + nextOpen.OpenTime.Value.TimeOfDay;
                    result.NextOpenDateTime = result.NextStatusChange;
                }
            }

            return result;
        }

        public async Task<ICollection<LocationFeature>> GetFullLocationFeaturesAsync(int locationId,
            bool forceReload)
        {
            string cacheKey = string.Format(CultureInfo.InvariantCulture,
                Utility.Keys.Cache.PromLocationFeatures,
                locationId);

            ICollection<LocationFeature> features = null;

            if (!forceReload)
            {
                features = await _cache.GetObjectFromCacheAsync<ICollection<LocationFeature>>(cacheKey);
            }

            if (features != null)
            {
                return features;
            }

            features = await _locationFeatureRepository.GetFullLocationFeaturesAsync(locationId);

            if (features == null)
            {
                return null;
            }

            await _cache.SaveToCacheAsync(cacheKey, features, CacheLocationFeatureHours);

            return features;
        }

        public async Task<List<LocationDayGrouping>> GetHoursAsync(int locationId,
            bool forceReload)
        {
            var location = await GetLocationAsync(locationId, forceReload);
            if (location?.IsAlwaysOpen != false || location.IsClosed)
            {
                return null;
            }
            var weeklyHours = await GetScheduleAsync(locationId, forceReload);
            return await ComputeWeeklyHoursAsync(weeklyHours, false);
        }

        public async Task<Location> GetLocationAsync(int id, bool forceReload)
        {
            string cacheKey = string.Format(CultureInfo.InvariantCulture,
                Utility.Keys.Cache.PromLocation,
                id);

            Location location = null;

            if (!forceReload)
            {
                location = await _cache.GetObjectFromCacheAsync<Location>(cacheKey);
            }

            if (location != null)
            {
                return location;
            }

            location = await _locationRepository.FindAsync(id);

            if (location == null)
            {
                return null;
            }

            location.DescriptionSegment = await _segmentService
                .GetSegmentTextBySegmentIdAsync(location.DescriptionSegmentId,
                forceReload);

            if (location.HoursSegmentId.HasValue)
            {
                location.HoursSegmentText = await _segmentService
                    .GetSegmentTextBySegmentIdAsync(location.HoursSegmentId.Value, forceReload);
            }

            if (location.PreFeatureSegmentId.HasValue)
            {
                location.PreFeatureSegmentText = await _segmentService
                    .GetSegmentTextBySegmentIdAsync(location.PreFeatureSegmentId.Value,
                        forceReload);
            }

            if (location.PostFeatureSegmentId.HasValue)
            {
                location.PostFeatureSegmentText = await _segmentService
                    .GetSegmentTextBySegmentIdAsync(location.PostFeatureSegmentId.Value,
                        forceReload);
            }

            if (location != null)
            {
                await _cache.SaveToCacheAsync(cacheKey, location, CacheLocationHours);
            }

            return location;
        }

        public async Task<LocationFeature> GetLocationFullFeatureAsync(int locationId,
            string featureSlug,
            bool forceReload)
        {
            string cacheKey = string.Format(CultureInfo.InvariantCulture,
                Utility.Keys.Cache.PromLocationFeature,
                locationId,
                featureSlug);

            LocationFeature locationFeature = null;

            if (!forceReload)
            {
                locationFeature = await _cache.GetObjectFromCacheAsync<LocationFeature>(cacheKey);
            }

            if (locationFeature != null)
            {
                return locationFeature;
            }

            locationFeature = await _locationFeatureRepository
                .GetFullLocationFeatureAsync(locationId, featureSlug);

            if (locationFeature != null)
            {
                await _cache.SaveToCacheAsync(cacheKey, locationFeature, CacheLocationFeatureHours);
            }

            return locationFeature;
        }

        public async Task<int?> GetLocationIdAsync(string slug, bool forceReload)
        {
            string cacheKey = string.Format(CultureInfo.InvariantCulture,
                Utility.Keys.Cache.PromLocationSlugToId,
                slug);

            int? id = null;

            if (!forceReload)
            {
                id = await _cache.GetIntFromCacheAsync(cacheKey);
            }

            if (id != null)
            {
                return id;
            }

            id = await _locationRepository.GetIdBySlugAsync(slug);

            if (id != null)
            {
                await _cache.SaveToCacheAsync(cacheKey, id, CacheSlugIdMapHours);
            }

            return id;
        }

        public async Task<Group> GetLocationsNeighborGroup(int groupId, bool forceReload)
        {
            string cacheKey = string.Format(CultureInfo.InvariantCulture,
                Utility.Keys.Cache.PromLocationNeighborGroup,
                groupId);

            Group group = null;

            if (!forceReload)
            {
                group = await _cache.GetObjectFromCacheAsync<Group>(cacheKey);
            }

            if (group != null)
            {
                return group;
            }

            group = await _groupRepository.FindAsync(groupId);

            if (group != null)
            {
                await _cache.SaveToCacheAsync(cacheKey, group, CacheLocationGroupHours);
            }

            return group;
        }

        public async Task<ICollection<LocationGroup>> GetLocationsNeighborsAsync(int groupId,
            bool forceReload)
        {
            string cacheKey = string.Format(CultureInfo.InvariantCulture,
                Utility.Keys.Cache.PromLocationGroup,
                groupId);

            ICollection<LocationGroup> locationGroups = null;

            if (!forceReload)
            {
                locationGroups = await _cache.GetObjectFromCacheAsync<ICollection<LocationGroup>>(cacheKey);
            }

            if (locationGroups == null)
            {
                locationGroups = await _locationGroupRepository.GetLocationsByGroupIdAsync(groupId);

                if (locationGroups != null)
                {
                    await _cache.SaveToCacheAsync(cacheKey, locationGroups, CacheLocationGroupHours);
                }
                else
                {
                    locationGroups = new List<LocationGroup>();
                }
            }

            foreach (var locationGroup in locationGroups.Where(_ => _.HasSubscription))
            {
                locationGroup.Location
                    = await GetLocationAsync(locationGroup.LocationId, forceReload);
            }

            return locationGroups;
        }

        public async Task<IList<Location>> GetLocationsStatusAsync(double? latitude,
            double? longitude)
        {
            var locations = await _locationRepository.GetAllLocations();

            foreach (var location in locations)
            {
                location.CurrentStatus = await GetCurrentStatusAsync(location.Id);
                if (latitude.HasValue
                    && longitude.HasValue
                    && !string.IsNullOrWhiteSpace(location.GeoLocation)
                    && location.GeoLocation.Contains(',', StringComparison.OrdinalIgnoreCase))
                {
                    var geolocation = location.GeoLocation.Split(',');
                    if (geolocation.Length == 2
                        && double.TryParse(geolocation[0], out var locationLatitude)
                        && double.TryParse(geolocation[1], out var locationLongitude))
                    {
                        location.Distance = Math.Ceiling(HaversineHelper.Calculate(
                                locationLatitude,
                                locationLongitude,
                                latitude.Value,
                                longitude.Value));
                    }
                }
            }

            return latitude.HasValue && longitude.HasValue
                ? locations.OrderBy(_ => _.Distance).ThenBy(_ => _.Name).ToList()
                : locations.OrderBy(_ => _.Name).ToList();
        }

        public async Task<ICollection<LocationDayGrouping>> GetStructuredHoursAsync(int locationId,
            bool forceReload)
        {
            var location = await GetLocationAsync(locationId, forceReload);
            if (location == null || location.IsAlwaysOpen || location.IsClosed)
            {
                return null;
            }
            var weeklyHours = await GetScheduleAsync(locationId, forceReload);
            return await ComputeWeeklyHoursAsync(weeklyHours, true);
        }

        public async Task<ICollection<LocationHoursResult>> GetWeeklyHoursAsync(int locationId)
        {
            var results = new List<LocationHoursResult>();

            var location = await _locationRepository.FindAsync(locationId);

            if (location.IsAlwaysOpen)
            {
                for (int day = 0; day < DaysInWeek; day++)
                {
                    results.Add(new LocationHoursResult
                    {
                        Open = true,
                        DayOfWeek = (DayOfWeek)day,
                        IsCurrentlyOpen = true
                    });
                }
            }
            else if (location.IsClosed)
            {
                for (int day = 0; day < DaysInWeek; day++)
                {
                    results.Add(new LocationHoursResult
                    {
                        Open = false,
                        DayOfWeek = (DayOfWeek)day,
                        IsCurrentlyOpen = false
                    });
                }
            }
            else
            {
                // Add override days
                var now = DateTime.Now;
                var firstDayOfWeek = now.AddDays(-(int)now.DayOfWeek);
                var lastDayOfWeek = firstDayOfWeek.AddDays(DaysInWeek - 1);

                var overrides = await _locationHoursOverrideRepository.GetBetweenDatesAsync(
                    locationId, firstDayOfWeek, lastDayOfWeek);

                foreach (var dayOverride in overrides)
                {
                    results.Add(new LocationHoursResult
                    {
                        OpenTime = dayOverride.OpenTime,
                        CloseTime = dayOverride.CloseTime,
                        Open = dayOverride.Open,
                        DayOfWeek = dayOverride.Date.DayOfWeek,
                        IsOverride = true
                    });
                }

                // Fill in non-override days
                if (results.Count < DaysInWeek)
                {
                    var weeklyHours
                        = await _locationHoursRepository.GetWeeklyHoursAsync(locationId);

                    var remainingDays = weeklyHours
                        .Where(_ => !results.Select(r => r.DayOfWeek).Contains(_.DayOfWeek));

                    foreach (var day in remainingDays)
                    {
                        results.Add(new LocationHoursResult
                        {
                            OpenTime = day.OpenTime,
                            CloseTime = day.CloseTime,
                            Open = day.Open,
                            DayOfWeek = day.DayOfWeek
                        });
                    }
                }

                // Set currently open
                foreach (var dayResult in results)
                {
                    if (dayResult.Open && dayResult.OpenTime <= now && dayResult.CloseTime >= now)
                    {
                        dayResult.IsCurrentlyOpen = true;
                    }
                }

                results = results.OrderBy(_ => _.DayOfWeek).ToList();
            }

            return results;
        }

        public async Task<string> GetZipCodeAsync(double latitude, double longitude)
        {
            return await _googleClient.GetZipCodeAsync(latitude, longitude);
        }

        private static string GetFormattedDayGroupings(List<DayOfWeek> days,
            bool isStructuredData = false)
        {
            var dayFormatter = new DateTimeFormatInfo();
            if (days.Count == 1)
            {
                return isStructuredData
                    ? dayFormatter.GetAbbreviatedDayName(days[0]).Substring(0, 2)
                    : dayFormatter.GetAbbreviatedDayName(days[0]);
            }
            else
            {
                var firstDay = days[0];
                var lastDay = days.Last();

                if (days.Count == 2)
                {
                    if (isStructuredData)
                    {
                        return dayFormatter.GetAbbreviatedDayName(firstDay)[..2]
                            + ndash + dayFormatter.GetAbbreviatedDayName(lastDay)[..2];
                    }
                    else
                    {
                        return dayFormatter.GetAbbreviatedDayName(firstDay)
                            + " & " + dayFormatter.GetAbbreviatedDayName(lastDay);
                    }
                }
                else if (days.Count == lastDay - firstDay + 1)
                {
                    if (isStructuredData)
                    {
                        return dayFormatter.GetAbbreviatedDayName(firstDay)[..2]
                            + ndash + dayFormatter.GetAbbreviatedDayName(lastDay)[..2];
                    }
                    else
                    {
                        return dayFormatter.GetAbbreviatedDayName(firstDay) +
                            $" {ndash} {dayFormatter.GetAbbreviatedDayName(lastDay)}";
                    }
                }
                else
                {
                    return string.Join(", ",
                        days.Select(_ => dayFormatter.GetAbbreviatedDayName(_)));
                }
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization",
                    "CA1308:Normalize strings to uppercase",
            Justification = "Display am or pm in lower-case for style reasons")]
        private async Task<List<LocationDayGrouping>>
            ComputeWeeklyHoursAsync(ICollection<LocationHours> weeklyHours, bool isStructuredData)
        {
            // Order weeklyHours to start on Monday
            weeklyHours = weeklyHours.OrderBy(_ => ((int)_.DayOfWeek + 6) % 7).ToList();

            var dayGroupings = new List<(List<DayOfWeek> DaysOfWeek,
                DateTime OpenTime,
                DateTime CloseTime)>();

            var closedDays = new List<DayOfWeek>();
            foreach (var day in weeklyHours)
            {
                if (day.Open)
                {
                    var (DaysOfWeek, OpenTime, CloseTime) = dayGroupings.LastOrDefault();
                    if (dayGroupings.Count > 0
                        && OpenTime == day.OpenTime
                        && CloseTime == day.CloseTime)
                    {
                        DaysOfWeek.Add(day.DayOfWeek);
                    }
                    else
                    {
                        dayGroupings.Add((
                            DaysOfWeek: new List<DayOfWeek> { day.DayOfWeek },
                            OpenTime: day.OpenTime.Value,
                            CloseTime: day.CloseTime.Value));
                    }
                }
                else
                {
                    closedDays.Add(day.DayOfWeek);
                }
            }

            var formattedDayGroupings = new List<LocationDayGrouping>();
            foreach (var (DaysOfWeek, OpenTime, CloseTime) in dayGroupings)
            {
                var days = isStructuredData ? GetFormattedDayGroupings(DaysOfWeek, true)
                    : GetFormattedDayGroupings(DaysOfWeek);

                var openTimeString = isStructuredData
                    ? new StringBuilder(OpenTime.ToString("%H", CultureInfo.InvariantCulture))
                    : new StringBuilder(OpenTime.ToString("%h", CultureInfo.InvariantCulture));

                if (OpenTime.Minute != 0 || isStructuredData)
                {
                    openTimeString.Append(OpenTime.ToString(":mm", CultureInfo.InvariantCulture));
                }

                openTimeString.Append(isStructuredData
                    ? ""
                    : OpenTime.ToString(" tt", CultureInfo.InvariantCulture).ToLowerInvariant());

                var closeTimeString = isStructuredData
                    ? new StringBuilder(CloseTime.ToString("%H", CultureInfo.InvariantCulture))
                    : new StringBuilder(CloseTime.ToString("%h", CultureInfo.InvariantCulture));

                if (CloseTime.Minute != 0 || isStructuredData)
                {
                    closeTimeString
                        .Append(CloseTime.ToString(":mm", CultureInfo.InvariantCulture));
                }
                closeTimeString.Append(isStructuredData
                    ? ""
                    : CloseTime.ToString(" tt", CultureInfo.InvariantCulture).ToLowerInvariant());

                formattedDayGroupings.Add(new LocationDayGrouping
                {
                    Days = days,
                    Time = $"{openTimeString} {ndash} {closeTimeString}"
                });
            }

            if (closedDays.Count > 0 && !isStructuredData)
            {
                var formattedClosedDays = GetFormattedDayGroupings(closedDays);
                formattedDayGroupings.Add(new LocationDayGrouping
                {
                    Days = formattedClosedDays,
                    Time = _localizer[i18n.Keys.Promenade.LocationClosed]
                });
            }

            return formattedDayGroupings;
        }

        private async Task<ICollection<LocationHours>> GetScheduleAsync(int locationId,
            bool forceReload)
        {
            string cacheKey = string.Format(CultureInfo.InvariantCulture,
                Utility.Keys.Cache.PromLocationWeeklyHours,
                locationId);

            ICollection<LocationHours> weeklyHours = null;

            if (!forceReload)
            {
                weeklyHours = await _cache
                    .GetObjectFromCacheAsync<ICollection<LocationHours>>(cacheKey);
            }

            if (weeklyHours != null)
            {
                return weeklyHours;
            }

            weeklyHours = await _locationHoursRepository.GetWeeklyHoursAsync(locationId);

            if (weeklyHours != null)
            {
                await _cache.SaveToCacheAsync(cacheKey, weeklyHours, CacheLocationScheduleHours);
            }

            return weeklyHours;
        }
    }
}