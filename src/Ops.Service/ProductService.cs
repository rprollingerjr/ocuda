﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ExcelDataReader;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Ocuda.Ops.Service.Abstract;
using Ocuda.Ops.Service.Filters;
using Ocuda.Ops.Service.Interfaces.Ops.Repositories;
using Ocuda.Ops.Service.Interfaces.Ops.Services;
using Ocuda.Ops.Service.Interfaces.Promenade.Repositories;
using Ocuda.Ops.Service.Interfaces.Promenade.Services;
using Ocuda.Promenade.Models.Entities;
using Ocuda.Utility.Abstract;
using Ocuda.Utility.Exceptions;
using Ocuda.Utility.Models;

namespace Ocuda.Ops.Service
{
    public class ProductService : BaseService<ProductService>, IProductService
    {
        private const string LocationNameHeading = "Location of test pickup?";
        private const string NumberOfItemsHeading = "Number of test kits distributed:";

        private readonly IDateTimeProvider _dateTimeProvider;
        private readonly ILocationService _locationService;

        private readonly IPermissionGroupProductManagerRepository
            _permissionGroupProductManagerRepository;

        private readonly IProductLocationInventoryRepository _productLocationInventoryRepository;
        private readonly IProductRepository _productRepository;
        private readonly IUserService _userService;

        public ProductService(ILogger<ProductService> logger,
            IHttpContextAccessor httpContextAccessor,
            IDateTimeProvider dateTimeProvider,
            ILocationService locationService,
            IPermissionGroupProductManagerRepository permissionGroupProductManagerRepository,
            IProductLocationInventoryRepository productLocationInventoryRepository,
            IProductRepository productRepository,
            IUserService userService)
            : base(logger, httpContextAccessor)
        {
            _dateTimeProvider = dateTimeProvider
                ?? throw new ArgumentNullException(nameof(dateTimeProvider));
            _locationService = locationService
                ?? throw new ArgumentNullException(nameof(locationService));
            _permissionGroupProductManagerRepository = permissionGroupProductManagerRepository
                ?? throw new ArgumentNullException(nameof(permissionGroupProductManagerRepository));
            _productLocationInventoryRepository = productLocationInventoryRepository
                ?? throw new ArgumentNullException(nameof(productLocationInventoryRepository));
            _productRepository = productRepository
                ?? throw new ArgumentNullException(nameof(productRepository));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
        }

        public async Task<ICollection<string>> BulkInventoryStatusUpdateAsync(int productId,
            bool addValues,
            IDictionary<int, int> adjustments)
        {
            if (adjustments == null)
            {
                throw new ArgumentNullException(nameof(adjustments));
            }

            var issues = new List<string>();
            var now = DateTime.Now;

            if (adjustments.Count > 0)
            {
                foreach (var adjustment in adjustments)
                {
                    if (adjustment.Value != 0)
                    {
                        var inventory = await _productLocationInventoryRepository
                            .GetByProductAndLocationAsync(productId, adjustment.Key);

                        int currentValue = inventory.ItemCount ?? 0;

                        if (addValues)
                        {
                            inventory.ItemCount = currentValue + adjustment.Value;
                        }
                        else
                        {
                            if (currentValue < adjustment.Value)
                            {
                                issues.Add($"Location {inventory.Location.Name}: count would have been less than 0, using 0");
                                inventory.ItemCount = 0;
                            }
                            else
                            {
                                inventory.ItemCount = currentValue - adjustment.Value;
                            }
                        }

                        inventory.UpdatedAt = now;
                        inventory.UpdatedBy = GetCurrentUserId();

                        _productLocationInventoryRepository.Update(inventory);
                    }
                }

                try
                {
                    await _productLocationInventoryRepository.SaveAsync();
                }
                catch (Exception ex)
                {
                    if (ex.InnerException != null)
                    {
                        issues.Add(ex.Message + "-" + ex.InnerException.Message);
                    }
                    else
                    {
                        issues.Add(ex.Message);
                    }
                }
            }
            else
            {
                issues.Add("There were no adjustments to be made.");
            }

            return issues;
        }

        public async Task<Product> GetByIdAsync(int productId)
        {
            return await _productRepository.GetByIdAsync(productId);
        }

        public async Task<ICollection<Product>> GetBySegmentIdAsync(int segmentId)
        {
            return await _productRepository.GetBySegmentIdAsync(segmentId);
        }

        public async Task<Product> GetBySlugAsync(string slug)
        {
            var product = await GetBySlugAsync(slug, false);
            var perms = await _permissionGroupProductManagerRepository
                .GetByProductIdAsync(product.Id);
            product.PermissionGroupIds = perms.Select(_ => _.PermissionGroupId
                .ToString(CultureInfo.InvariantCulture));
            return product;
        }

        public async Task<Product> GetBySlugAsync(string slug, bool ignoreActiveFlag)
        {
            if (string.IsNullOrEmpty(slug))
            {
                throw new ArgumentNullException(nameof(slug));
            }

            var formattedSlug = slug
                .Trim()
                .ToLower(System.Globalization.CultureInfo.CurrentCulture);

            return ignoreActiveFlag
                ? await _productRepository.GetBySlugAsync(formattedSlug)
                : await _productRepository.GetActiveBySlugAsync(formattedSlug);
        }

        public async Task<ProductLocationInventory> GetInventoryByProductAndLocationAsync(int productId,
            int locationId)
        {
            var inventory = await _productLocationInventoryRepository.GetByProductAndLocationAsync(
                productId, locationId);

            if (inventory.UpdatedBy.HasValue)
            {
                var updatedBy = await _userService.GetNameUsernameAsync(inventory.UpdatedBy.Value);
                inventory.UpdatedByName = updatedBy.Name;
                inventory.UpdatedByUsername = updatedBy.IsDeleted ? null : updatedBy.Username;
            }

            if (inventory.ThreshholdUpdatedBy.HasValue)
            {
                var threshholdUpdatedBy = await _userService
                    .GetNameUsernameAsync(inventory.ThreshholdUpdatedBy.Value);
                inventory.ThreshholdUpdatedByName = threshholdUpdatedBy.Name;
                inventory.ThreshholdUpdatedByUsername = threshholdUpdatedBy.IsDeleted
                    ? null
                    : threshholdUpdatedBy.Username;
            }

            return inventory;
        }

        public async Task<ICollection<ProductLocationInventory>>
                    GetLocationInventoriesForProductAsync(int productId)
        {
            var inventories = await _productLocationInventoryRepository
                .GetForProductAsync(productId);

            foreach (var inventory in inventories)
            {
                if (inventory.UpdatedBy.HasValue)
                {
                    var updatedBy = await _userService
                        .GetNameUsernameAsync(inventory.UpdatedBy.Value);
                    inventory.UpdatedByName = updatedBy.Name;
                    inventory.UpdatedByUsername = updatedBy.IsDeleted ? null : updatedBy.Username;
                }
            }

            return inventories;
        }

        public async Task<CollectionWithCount<Product>> GetPaginatedListAsync(BaseFilter filter)
        {
            var products = await _productRepository.GetPaginatedListAsync(filter);
            foreach (var product in products.Data)
            {
                var perms = await _permissionGroupProductManagerRepository
                    .GetByProductIdAsync(product.Id);
                product.PermissionGroupIds = perms.Select(_ => _.PermissionGroupId
                    .ToString(CultureInfo.InvariantCulture));
            }

            return products;
        }

        public async Task LinkSegment(int productId, int segmentId)
        {
            var product = await _productRepository.GetByIdAsync(productId);
            if (product == null)
            {
                throw new OcudaException($"Unable to find product id {productId}");
            }
            product.SegmentId = segmentId;
            _productRepository.Update(product);
            await _productRepository.SaveAsync();
        }

        public async Task<IDictionary<int, int>> ParseInventoryAsync(int productId, string filename)
        {
            var inventory = new Dictionary<int, int>();

            var locations = (await _locationService.GetAllLocationsAsync())
                .ToDictionary(k => k.Name, v => v.Id);

            var locationMap = await _locationService.GetLocationProductMapAsync(productId);

            var locationIssues = new Dictionary<string, int>();
            var issues = new Dictionary<string, string>();

            using (var stream = new System.IO.FileStream(filename, System.IO.FileMode.Open))
            {
                int locationNameColId = 0;
                int numberOfItemsColId = 0;

                int rows = 0;

                using var excelReader = ExcelReaderFactory.CreateReader(stream);
                while (excelReader.Read())
                {
                    rows++;
                    if (rows == 1)
                    {
                        try
                        {
                            for (int col = 0; col < excelReader.FieldCount; col++)
                            {
                                switch (excelReader.GetString(col).Trim() ?? $"Column{col}")
                                {
                                    case LocationNameHeading:
                                        locationNameColId = col;
                                        break;

                                    case NumberOfItemsHeading:
                                        numberOfItemsColId = col;
                                        break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            throw new OcudaException($"Unable to find column: {ex.Message}", ex);
                        }
                    }
                    else
                    {
                        try
                        {
                            var location = excelReader.GetString(locationNameColId);

                            if (string.IsNullOrWhiteSpace(location))
                            {
                                continue;
                            }

                            var count = excelReader.GetDouble(numberOfItemsColId);

                            if (string.IsNullOrEmpty(location))
                            {
                                issues.Add($"Empty location on row {rows}", null);
                                continue;
                            }

                            int? locationId = null;

                            if (locations.ContainsKey(location.Trim()))
                            {
                                locationId = locations[location.Trim()];
                            }
                            else if (locationMap.ContainsKey(location.Trim()))
                            {
                                locationId = locationMap[location.Trim()];
                            }

                            if (!locationId.HasValue)
                            {
                                if (locationIssues.ContainsKey(location))
                                {
                                    locationIssues[location]++;
                                }
                                else
                                {
                                    locationIssues.Add(location, 1);
                                }
                                continue;
                            }

                            if (inventory.ContainsKey(locationId.Value))
                            {
                                inventory[locationId.Value] += Convert.ToInt32(count);
                            }
                            else
                            {
                                inventory.Add(locationId.Value, Convert.ToInt32(count));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex,
                                "Unable to import row {Row}: {ErrorMessage}",
                                rows,
                                ex.Message);
                            issues.Add($"Unable to import row {rows}: {ex.Message}", null);
                        }
                    }
                }
            }

            foreach (var locationIssue in locationIssues)
            {
                issues.Add($"Location '{locationIssue.Key}' could not be mapped on {locationIssue.Value} rows",
                    locationIssue.Key);
            }

            if (issues.Count > 0)
            {
                var ex = new OcudaException("One or more errors were found during the import");
                ex.Data.Add("Issues", issues);
                ex.Data.Add("Inventory", inventory);
                throw ex;
            }

            return inventory;
        }

        public async Task SetActiveLocation(string productSlug, int locationId, bool isActive)
        {
            if (string.IsNullOrEmpty(productSlug))
            {
                throw new ArgumentNullException(nameof(productSlug));
            }

            var product = await _productRepository.GetBySlugAsync(productSlug);

            if (product == null)
            {
                throw new OcudaException($"Can't find product: {productSlug}");
            }

            if (isActive)
            {
                await _productLocationInventoryRepository.AddAsync(new ProductLocationInventory
                {
                    CreatedAt = _dateTimeProvider.Now,
                    CreatedBy = GetCurrentUserId(),
                    ItemCount = 0,
                    LocationId = locationId,
                    ProductId = product.Id,
                    UpdatedAt = _dateTimeProvider.Now,
                    UpdatedBy = GetCurrentUserId()
                });
            }
            else
            {
                var productLocationInventory = await _productLocationInventoryRepository
                    .GetByProductAndLocationAsync(product.Id, locationId);
                _productLocationInventoryRepository.Remove(productLocationInventory);
            }
            await _productLocationInventoryRepository.SaveAsync();
        }

        public async Task UnlinkSegment(int productId)
        {
            var product = await _productRepository.GetByIdAsync(productId);
            if (product == null)
            {
                throw new OcudaException($"Unable to find product id {productId}");
            }
            product.SegmentId = null;
            _productRepository.Update(product);
            await _productRepository.SaveAsync();
        }

        public async Task UpdateInventoryStatusAsync(int productId, int locationId, int itemCount)
        {
            var currentStatus = await _productLocationInventoryRepository
                .GetByProductAndLocationAsync(productId, locationId);

            currentStatus.ItemCount = itemCount;
            currentStatus.UpdatedAt = _dateTimeProvider.Now;
            currentStatus.UpdatedBy = GetCurrentUserId();

            _productLocationInventoryRepository.Update(currentStatus);
            await _productLocationInventoryRepository.SaveAsync();
        }

        public async Task<Product> UpdateProductAsync(Product product)
        {
            if (product == null) { throw new ArgumentNullException(nameof(product)); }
            var currentProduct = await _productRepository.GetByIdAsync(product.Id);

            if (currentProduct == null)
            {
                throw new OcudaException($"Unable to find product id {product.Id}");
            }

            currentProduct.CacheInventoryMinutes = product.CacheInventoryMinutes;
            currentProduct.IsActive = product.IsActive;
            currentProduct.IsVisibleToPublic = product.IsVisibleToPublic;
            currentProduct.Name = product.Name?.Trim();
            currentProduct.UpdatedAt = _dateTimeProvider.Now;
            currentProduct.UpdatedBy = GetCurrentUserId();

            _productRepository.Update(currentProduct);
            await _productRepository.SaveAsync();
            return currentProduct;
        }

        public async Task UpdateThreshholdAsync(int productId, int locationId, int threshholdValue)
        {
            var currentStatus = await _productLocationInventoryRepository
                .GetByProductAndLocationAsync(productId, locationId);

            currentStatus.ManyThreshhold = threshholdValue;
            currentStatus.ThreshholdUpdatedAt = _dateTimeProvider.Now;
            currentStatus.ThreshholdUpdatedBy = GetCurrentUserId();

            _productLocationInventoryRepository.Update(currentStatus);
            await _productLocationInventoryRepository.SaveAsync();
        }
    }
}
