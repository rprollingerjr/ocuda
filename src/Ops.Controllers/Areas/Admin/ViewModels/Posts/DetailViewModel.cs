﻿using Ocuda.Ops.Models;

namespace Ocuda.Ops.Controllers.Areas.Admin.ViewModels.Posts
{
    public class DetailViewModel
    {
        public Post Post { get; set; }
        public string Action { get; set; }
        public int SectionId { get; set; }
        public bool IsDraft { get; set; }
    }
}
