# Microsoft.PowerShell.ODataUtils Module
[Microsoft.PowerShell.ODataUtils module](https://technet.microsoft.com/en-us/library/dn818507.aspx) generates CDXML modules that contain cmdlets to manage [OData](http://www.odata.org/) and [Redfish](https://www.dmtf.org/standards/redfish) endpoints.

|Master   |
|:------:|
|[![Build status](https://ci.appveyor.com/api/projects/status/7keb7k1fdlqqhfq2/branch/master?svg=true)](https://ci.appveyor.com/project/PowerShell/odatautils/branch/master)|

# Building
1. [Ensure that .NET Command Line Interface tools are installed.](https://github.com/PowerShell/PowerShell/blob/master/docs/building/windows-core.md#net-cli)
2. Run `build.ps1`
3. Successfull build will generate `Microsoft.PowerShell.ODataUtils` module folder that can be copied to a target PowerShell deployment.

# Examples
## Using Redfish server from PowerShell Core
```
Import-Module Microsoft.PowerShell.ODataUtils -Force
# generate CDXML module based on metadata of Redfish server
Export-ODataEndpointProxy -Uri 'https://<redfishserver>/redfish/v1/' -OutputModule '/home/test/RedfishModule' -Force -CmdletAdapter ODataV4Adapter -AllowUnsecureConnection -SkipCertificateCheck
Import-Module '/home/test/RedfishModule' -Force
$c = Get-Credential # credentials for server
# manage server using Redfish - retrieve ComputerSystems on the server
Get-ServiceRoot -SkipCertificateCheck | Get-ComputerSystemCollection -Credential $c -SkipCertificateCheck | Get-ComputerSystem -Credential $c -SkipCertificateCheck
```
## Using Redfish server from Windows PowerShell
```
Import-Module Microsoft.PowerShell.ODataUtils -Force
# some preparation work for connection to a Redfish endpoint
# enable TLS v1.1 as required by Redfish spec
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor [System.Net.SecurityProtocolType]::Tls11
# allow self-signed server certificates
if ([System.Net.ServicePointManager]::CertificatePolicy.GetType().Name -ne 'TrustAllCertsPolicy')
{
    Add-Type 'using System.Net;using System.Security.Cryptography.X509Certificates;public class TrustAllCertsPolicy:ICertificatePolicy {public bool CheckValidationResult(ServicePoint srvPoint, X509Certificate certificate,WebRequest request, int certificateProblem) {return true;}}'
    [System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAllCertsPolicy
}
# Generate CDXML module based on endpoint metadata
Export-ODataEndpointProxy -Uri https://<redfishserver>/redfish/v1/ -OutputModule C:\Temp\RedfishTest -Force -CmdletAdapter ODataV4Adapter -AllowUnsecureConnection
$c = Get-Credential # credentials for server
Import-Module C:\Temp\RedfishTest –Force
# manage server using Redfish
# example 1 - retrieve ComputerSystems on the server
$r = Get-ServiceRoot
$cc = Get-ComputerSystemCollection -ServiceRoot $r -Credential $c
$cs = Get-ComputerSystem -ComputerSystemCollection $cc -Credential $c
$cs
# example 2 - retrieve Chassis on the server
Get-ServiceRoot | Get-ChassisCollection -Credential $c | Get-Chassis -Credential $c
```
