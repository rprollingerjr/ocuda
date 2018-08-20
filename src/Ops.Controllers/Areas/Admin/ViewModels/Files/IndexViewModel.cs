﻿using System.Collections.Generic;
using System.ComponentModel;
using Ocuda.Ops.Models;
using Ocuda.Utility.Models;

namespace Ocuda.Ops.Controllers.Areas.Admin.ViewModels.Files
{
    public class IndexViewModel
    {
        public IEnumerable<File> Files { get; set; }
        public IEnumerable<Category> Categories { get; set; }
        public PaginateModel PaginateModel { get; set; }
        public string CategoryName { get; set; }
        [DisplayName("Category")]
        public string CategoryId { get; set; }
        public File File { get; set; }
    }
}
