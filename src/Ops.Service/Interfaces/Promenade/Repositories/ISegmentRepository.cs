﻿using System.Collections.Generic;
using System.Threading.Tasks;
using Ocuda.Ops.Service.Filters;
using Ocuda.Ops.Service.Interfaces.Ops.Repositories;
using Ocuda.Ops.Service.Models;
using Ocuda.Promenade.Models.Entities;

namespace Ocuda.Ops.Service.Interfaces.Promenade.Repositories
{
    public interface ISegmentRepository : IRepository<Segment, int>
    {
        Task<DataWithCount<ICollection<Segment>>> GetPaginatedListAsync(
            BaseFilter filter);

        Segment FindSegmentByName(string name);

        Task<bool> IsDuplicateNameAsync(Segment segment);
    }
}
