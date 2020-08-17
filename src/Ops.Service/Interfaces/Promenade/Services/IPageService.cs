﻿using System.Collections.Generic;
using System.Threading.Tasks;
using Ocuda.Ops.Service.Filters;
using Ocuda.Promenade.Models.Entities;
using Ocuda.Utility.Models;

namespace Ocuda.Ops.Service.Interfaces.Promenade.Services
{
    public interface IPageService
    {
        Task<Page> GetByHeaderAndLanguageAsync(int headerId, int languageId);
        Task<Page> CreateAsync(Page page);
        Task<Page> EditAsync(Page page);
        Task DeleteAsync(Page page);
        Task<DataWithCount<ICollection<PageHeader>>> GetPaginatedHeaderListAsync(PageFilter filter);
        Task<PageHeader> GetHeaderByIdAsync(int id);
        Task<ICollection<string>> GetHeaderLanguagesByIdAsync(int id);
        Task<PageHeader> CreateHeaderAsync(PageHeader header);
        Task<PageHeader> EditHeaderAsync(PageHeader header);
        Task DeleteHeaderAsync(int id);
        Task<bool> StubInUseAsync(PageHeader header);
    }
}
