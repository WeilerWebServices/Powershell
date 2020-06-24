// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System.ServiceProcess;

namespace TestService
{
    static class Program
    {
       static void Main()
        {
            ServiceBase[] ServicesToRun;
            ServicesToRun = new ServiceBase[]
            {
                new Service1()
            };
            ServiceBase.Run(ServicesToRun);
        }
    }
}
