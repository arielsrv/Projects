﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AutomationTestAssistantCore
{
    public class ProjectWorker
    {
        public ProjectBuilder ProjectBuilder
        {
            get
            {
                return new ProjectBuilder();
            }
        }

        public ProjectInfoCollector ProjectInfoCollector
        {
            get
            {
                return new ProjectInfoCollector();
            }
        }
    }
}
