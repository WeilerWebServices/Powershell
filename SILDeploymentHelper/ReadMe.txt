#SIL Deployment Helper Module
This module contains four scripts to help with deploying Windows Server Software Inventory Logging (SIL) at scale.
 1. Enable-SILCollector
 2. Enable-SILCollectorVHD
 3. Enable-SILCollectorWithWindowsSetup
 4. Set-SILAPollingAccount

Note: The term ‘Collector’ refers to the Windows Server feature Software Inventory Logging (SIL) component of the overall SIL framework.

The first step is to copy this module down locally and then import it into a PowerShell console opened as an administrator using the Import-Module Cmdlet.  This can be done from any Windows client or server running a current version of PowerShell, and which is on your infrastructure's network.

#### Prerequisites
1. PowerShell remoting must be enabled on both the SIL Aggregator server and the SIL Collector server.
1. Current user must have Administrator rights on both the SIL Aggregator server and SIL Collector server.
1. Current user must be able to execute SIL Aggregator PowerShell cmdlets remotely from current server. This script will run the following two SIL Aggregator cmdlets remotely – 
  1. Get-SILAggregator – to get the ‘TargetUri’ value
  1. Set-SILAggregator -  to set the certificate thumbprint 
1. The SIL Collector server must have the required updates installed
  1. For Windows Server 2012 R2
    * KB3000850, Nov 2014 
    * KB3060681, June 2015
  1. For Windows Server 2012 
    * KB3119938 (requires WMF 4.0)
  1. For Windows Server 2008 R2 SP1
    * KB3109118 (requires .Net 4.5 and WMF 4.0)
1. The client certificate type is .PFX and not of any other format.
2. For functions (2 & 3) that modify a VHD, administrator access to the server that holds the VHD is required. 



==========================
--------------------------
##1. Enable-SILCollector
--------------------------
This function will enable SIL, on a remote server, to publish inventory data to a SIL Aggregator.  This script can be executed in a loop to configure SIL on multiple computers (Windows Servers only).

===
####Example:
    $pwd = ConvertTo-SecureString -String 'yourcertificatepassword' -AsPlainText -Force
    
    Enable-SilCollector -SilCollectorServer "yourremoteservertobeinventoried" -SilCollectorServerCredential "domain\user" -SilAggregatorServer "yourSILAggregatorMachineName" -SilAggregatorServerCredential "domain\user" -CertificateFilePath "\\yourshare\yourvalidSSLcertificate.pfx" -CertificatePassword $pwd
    
===
####Parameters:
| Parameter Name      | Type        | Required  | Description |
|:---|:---|:---|:---|
| SilCollectorServer     | String  |Y	 |Specifies a remote server to be enabled and configured for Software Inventory Logging.|	 
|SilCollectorServerCredential|PSCredential|N|Specifies the credentials that allow this script to connect to the remote SIL Collector server.|
|SilAggregatorServer|String|Y|Specifies the SIL Aggregator server. This server must have Software Inventory Logging Aggregator installed|
|SilAggregatorServerCredential|PSCredential|N|Specifies the credentials that allow this script to connect to the remote SIL Aggregator server.|
|CertificateFilePath|String|Y|Specifies the directory path for the PFX file.|
|CertificatePassword|SecureString|Y|Specifies the password for the imported PFX file in the form of a secure string. **Passwords must be passed in Secure String format**|


Notes: 
 * To obtain a PSCredential object, use the ‘Get-Credential’ Cmdlet. For more information, type Get-Help Get-Credential.
 * For passwords use ConvertTo-SecureString Cmdlet.  Example: $pwd = ConvertTo-SecureString -String 'yourpassword' -AsPlainText -Force 

===
####Error Messages:
| Possible Errors      | Reason |
|:---|:---|
|Error!!! login using admin credentials.|Script is executing from non-admin PS prompt.|
|Error!!! [$CertificateFilePath] is invalid.|Certificate Path on Local System is not valid or accessible.|
|Cannot validate argument on parameter CertificateFilePath. The certificate must be of '.PFX' format.|The client certificate type is not .PFX format.|
|Certificate Password is Incorrect.|Certificate password is incorrect.|
|Required Windows Update(s) are not installed on [$SilCollectorServer].|The SIL Collector server does not have required SIL updates installed.|
|Error!!! Software Inventory Logging Aggregator 1.0 is not installed on [$AggregatorServer].| The SILA Server does not have Software Inventory Logging Aggregator installed.|
|Error in connecting to Aggregator server[$AggregatorServer].|The SIL Aggregator Server is not accessible.|
|Error in connecting to remote server [$SilCollectorServer].|The SIL Collector server is not accessible.|


===
####Tasks performed by Enable-SILCollector:
1. Update TrustedHosts settings, if needed, of current Local Computer by adding the SIL Collector server and SIL Aggregator 
Server to trusted hosts list.
2. Copy the pfx client certificate to SIL Collector server.
3. Install Certificate at  \localmachine\MY (Local Computer -> Personal) at SIL Collector server.
4. Get the ‘TargetURI’ value by running the PowerShell cmdlet ‘Get-SILAggregator’ on SIL Aggregator server .
5. Get the certificate thumbprint value from the provided .PFX certificate file.
6. Configure SIL on SIL Collector server by – 
   1) Run ‘Set-SILLogging’ with parameters – ‘TargetUri’ and ‘CertificateThumbprint’
   2) Run ‘Start-SILLogging’ 
7. Run ‘Set-SILAggregator’ on SIL Aggregator server to register certificate thumbprint from step 5 above.
8. Delete the PFX certificate which was copied earlier from SIL Collector server.
9. Validate the SIL configuration by running Publish-SILData cmdlet on remote computer.
10. Revert the TrustedHosts settings updated in step 1.



  

===========================
----------------------------
##2. Enable-SILCollectorVHD
----------------------------
This function will setup and enable Software Inventory Logging in a Virtual Hard Disk with Windows Server already installed.	

This function can be used to setup Software Inventory Logging in a Virtual Hard Disk so that all VMs created using this VHD will have SIL already configured.

The practical uses for this are intended to cover both ‘gold image’ setup for wide deployment across data centers, as well as configuring end user images for cloud deployment.

===
####Design:
Configuring SIL in a VHD involves two parts –
* Part 1 – Install an enterprise cert on the VHD to be used for SIL communication with the SIL Aggregator.
* Part 2 – Ensure, on every VM created from this VHD, SIL is started and configured to send inventory data to the SIL Aggregator server at regular intervals.

===
####Example:
    $pwd = ConvertTo-SecureString -String 'yourcertificatepassword' -AsPlainText -Force
    
    Enable-SilCollectorVHD -VirtualHardDiskPath "\yourdirectory\share" -SilAggregatorServer "yourSILAggregatorMachineName" -SilAggregatorServerCredential "domain\user" -CertificateFilePath "\\yourshare\yourvalidSSLcertificate.pfx" -CertificatePassword $pwd

===
####Parameters:
| Parameter Name      | Type        | Required  | Description |
|:---|:---|:---|:---|
|VirtualHardDiskPath|String|Y|Specifies the path for a Virtual Hard Disk to be configured. BothVHD and VHDX formats are valid. The Windows Server operating system contained within this VHD must Have SIL feature installed (see prerequisites)|	 
|SilAggregatorServer|String|Y|Specifies the SIL Aggregator server. This server must have Software Inventory Logging Aggregator installed|
|SilAggregatorServerCredential|PSCredential|N|Specifies the credentials that allow this script to connect to the remote SIL Aggregator server.|
|CertificateFilePath|String|Y|Specifies the directory path for the PFX file.|
|CertificatePassword|SecureString|Y|Specifies the password for the imported PFX file in the form of a secure string. **Passwords must be passed in Secure String format**|


Notes: 
 * To obtain a PSCredential object, use the ‘Get-Credential’ Cmdlet. For more information, type Get-Help Get-Credential.
 * For passwords use ConvertTo-SecureString Cmdlet.  Example: $pwd = ConvertTo-SecureString -String 'yourpassword' -AsPlainText -Force 

===
####Error Messages:
| Possible Errors      | Reason |
|:---|:---|
|Error!!! login using admin credentials.|Script is executing from non-admin PS prompt.|
|Error!!! [$CertificateFilePath] is invalid.|Certificate Path on Local System is not valid or accessible.|
|Cannot validate argument on parameter CertificateFilePath. The certificate must be of '.PFX' format.|The client certificate type is not .PFX format.|
|Certificate Password is Incorrect.|Certificate password is incorrect.|
|Required Windows Update(s) are not installed on VirtualHardDisk.|The VHD does not have required SIL updates installed.|
|Cannot validate argument on parameter VirtualHardDiskPath. The VHD File Path must be of '.vhd or .vhdx' format.|The VHD File Path type is not .vhd/.vhdx format.|
|Error!!! Only Reporting Module is found on [$SilAggregatorServer]. Install Software Inventory Logging Aggregator|The SIL Aggregator Server only has Software Inventory Logging Reporting Module installed.|
|Error!!! Software Inventory Logging Aggregator 1.0 is not installed on [$AggregatorServer].| The SILA Server does not have Software Inventory Logging Aggregator installed.|
|Error in connecting to Aggregator server[$AggregatorServer].|The SIL Aggregator Server is not accessible.|
|VHDFile is being used by another process.|VHD File is in use.|
|Software Inventory Logging feature is not found. The VHD may have the Operating System which does not support SIL.|VHD File doesn’t have Software Inventory Logging feature.|



===
####Tasks performed by Enable-SILCollectorVHD:

#####Part 1 
To make sure that the given enterprise cert is installed in all VMs created using the SIL configured VHD, this script modifies the ‘RunOnce’ registry key of the VHD, and sets another dynamically generated script to execute when a Administrator user logs in to the VM first time.
 1. Checks the certificate Password and get the certificate thumbprint value from the provided .PFX certificate file.
 2. Updates Trusted Hosts settings of Local Computer by adding the Aggregator Server to trusted hosts list, if required.
 3. Checks if Software Inventory Logging Aggregator is installed on Aggregator Server and get the ‘TargetURI’ value by running the PowerShell cmdlet ‘Get-SILAggregator’ remotely on Aggregator server. 
 4. Mounts the VHD
   Mount-VHD -Path $VirtualHardDiskPath
 5. Loads Registry from VHD 
   $RemoteReg=$DriveLetter + ":\Windows\System32\config\Software"
   REG LOAD 'HKLM\REMOTEPC' $RemoteReg
 6. Copy cert file to VHD at “\Scripts”
   Copy-Item -Path $CertificateFilePath -Destination $remoteCert
 7. The script will prepare another .cmd file at run time to import certificate in \localmachine\MY (Local Computer -> Personal) store on the current running system. This script will run automatically on the VM to install certificate using required parameters. 

   * Set-Variable -Name psPath -Value "%windir%\System32\WindowsPowerShell\v1.0\powershell.exe" -Option Constant
   * Set-Variable -Name certStore -Value "cert:\localmachine\MY" -Option Constant

   Encrypt SecureString Password for Certificate to be installed
   * $encCertPswd = ConvertFrom-SecureString -SecureString $CertificatePassword -Key (1..16) 

   Create a command to import certificate and write it on “EnableSIL.cmd” file
   * $cmd = [string]::Format("{0} -CertStoreLocation {1} -FilePath {2} -Password (convertto-securestring -key (1..16) -string     {3})", "Import-PfxCertificate", $certStore, $certFile, $encCertPswd) 
       
   * $cmd1 = [string]::Format("{0} -command {1}", $pspath, $cmd)
   * Add-Content $SetupFilePath $cmd1 

   Add another command to remove the certificate file.
   * $cmd = [string]::Format("{0} {1} -ErrorAction Stop", "Remove-Item", $certFile) 
   * $cmd2 = [string]::Format("{0} -command {1}", $pspath, $cmd) 
   * Add-Content $SetupFilePath "`n$cmd2"

   Add another command to remove EnableSIL.cmd file.
   * $cmd = [string]::Format("{0} {1} -ErrorAction Stop", "Remove-Item", $filePath)  
   * $cmd3 = [string]::Format("{0} -command {1}", $pspath, $cmd) 
   * Add-Content $SetupFilePath "`n$cmd3" 

  8.Sets above dynamically generated EnableSIL.cmd file to a ‘RunOnce’ Registry key in VHD to execute this above script for every VM on first time start.

   * HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\RunOnce
   * Set-ItemProperty "HKLM:\REMOTEPC\Microsoft\Windows\CurrentVersion\RunOnce\" -Name "PoshStart" -Value "C:\Scripts\EnableSIL.cmd"

#####Part 2
Loads and edits Software Inventory Logging registry entries – 
\HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\SoftwareInventoryLogging. 

|Function|Value Name|Data|Corresponding Cmdlet (available only in running OS)|
|:---|:---|:---|:---|
|Start/Stop Feature|CollectionState|1 or 0|Start-SilLogging, Stop-SilLogging|
|Specifies SIL Aggregator on the network|TargetUri|String|Set-SilLogging -TargetURI|
|Specifies certificate thumbprint of the certificate used for SSL authentication at the SIL Aggregator|CertificateThumbprint|String|Set-SilLogging -CertificateThumbprint|
|Optionally specifies date and time for start (if in the future)|CollectionTime|Default: start now|Set-SilLogging -TimeOfDay|

More information on configuration settings for SIL can be found here: https://technet.microsoft.com/en-us/library/dn383584.aspx

		
9. Sets the following Registry key values in VHD as following – 
   * CollectionState: 		1
   * TargetUri:			Value received from Step 3 pt. 1
   * CertificateThumbprint:	Value reeived from Step 1, pt. 1

10. Runs ‘Set-SILAggregator -addCertificateThumbprint’ on the Aggregator server to register certificate thumbprint from step 1, pt. 1.
11. Revert back the TrustedHosts settings updated in step 2, pt. 1.




===========================
-------------------------------------
##3. Enable-SILCollectorWithWindowsSetup
-------------------------------------
This function will also setup Software Inventory Logging in a Virtual Hard Disk, but leverages the Windows automated setup process instead of registry keys.  One method, or the other, will be more appropriate depending on cloud deployment practices and infrastructure.

The practical uses for this are intended to cover both ‘gold image’ setup for wide deployment across data centers, as well as configuring end user images for cloud deployment.

===
####Design:
Configuring SIL in a VHD involves two parts –
* Part 1 – Install an enterprise cert on the VHD to be used for SIL communication with the SIL Aggregator.
* Part 2 – Ensure, on every VM created from this VHD, SIL is started and configured to send inventory data to the SIL Aggregator server at regular intervals.

This function creates or modifies ‘%WINDIR%\Setup\Scripts\SetupComplete.cmd’ file in the VHD to enable and configure SIL. When a new VM is created using the VHD, the Software Inventory Logging is configured after Windows is installed, but before the logon screen appears.

===
####Example:
    $pwd = ConvertTo-SecureString -String 'yourcertificatepassword' -AsPlainText -Force
    
    Enable-SilCollectorWithWindowsSetup -VirtualHardDiskPath "\yourdirectory\share" -SilAggregatorServer "yourSILAggregatorMachineName" -SilAggregatorServerCredential "domain\user" -CertificateFilePath "\\yourshare\yourvalidSSLcertificate.pfx" -CertificatePassword $pwd
    
===
####Parameters:
| Parameter Name      | Type        | Required  | Description |
|:---|:---|:---|:---|
|VirtualHardDiskPath|String|Y|Specifies the path for a Virtual Hard Disk to be configured. BothVHD and VHDX formats are valid. The Windows Server operating system contained within this VHD must Have SIL feature installed (see prerequisites)|	 
|SilAggregatorServer|String|Y|Specifies the SIL Aggregator server. This server must have Software Inventory Logging Aggregator installed|
|SilAggregatorServerCredential|PSCredential|N|Specifies the credentials that allow this script to connect to the remote SIL Aggregator server.|
|CertificateFilePath|String|Y|Specifies the directory path for the PFX file.|
|CertificatePassword|SecureString|Y|Specifies the password for the imported PFX file in the form of a secure string. **Passwords must be passed in Secure String format**|
Notes: 
 * To obtain a PSCredential object, use the ‘Get-Credential’ Cmdlet. For more information, type Get-Help Get-Credential.
 * For passwords use ConvertTo-SecureString Cmdlet.  Example: $pwd = ConvertTo-SecureString -String 'yourpassword' -AsPlainText -Force 

===
####Error Messages:
| Possible Errors      | Reason |
|:---|:---|
|Error!!! login using admin credentials.|Script is executing from non-admin PS prompt.|
|Error!!! [$CertificateFilePath] is invalid.|Certificate Path on Local System is not valid or accessible.|
|Cannot validate argument on parameter CertificateFilePath. The certificate must be of '.PFX' format.|The client certificate type is not .PFX format.|
|Certificate Password is Incorrect.|Certificate password is incorrect.|
|Required Windows Update(s) are not installed on VirtualHardDisk.|The VHD does not have required SIL updates installed.|
|Cannot validate argument on parameter VirtualHardDiskPath. The VHD File Path must be of '.vhd or .vhdx' format.|The VHD File Path type is not .vhd/.vhdx format.|
|Error!!! Only Reporting Module is found on [$SilAggregatorServer]. Install Software Inventory Logging Aggregator|The SIL Aggregator Server only has Software Inventory Logging Reporting Module installed.|
|Error!!! Software Inventory Logging Aggregator 1.0 is not installed on [$AggregatorServer].| The SILA Server does not have Software Inventory Logging Aggregator installed.|
|Error in connecting to Aggregator server[$AggregatorServer].|The SIL Aggregator Server is not accessible.|
|VHDFile is being used by another process.|VHD File is in use.|
|Software Inventory Logging feature is not found. The VHD may have the Operating System which does not support SIL.|VHD File doesn’t have Software Inventory Logging feature.|



===
####Tasks performed by Enable-SILCollectorWithWindowsSetup

To make sure that the given enterprise cert is installed on all VMs created using the SIL configured VHD, this script modifies or add the ‘SetupComplete.cmd’ file on the VHD.

1. Validate if required SIL updates are installed or not in the given VHD. If not, then display a warning message.
2. If required, Update TrustedHosts settings of Current Computer where this script is running by adding the Aggregator Server to trusted hosts list.
3. Copy input Enterprise cert file to VHD at “‘%WINDIR%\Setup\Scripts ”. This cert file will be installed at the time of VM creation.
4. Get the SIL Aggregation Server URI, ‘TargetURI’ value by running the PowerShell cmdlet ‘Get-SILAggregator’ remotely on the Aggregator server.
5. Get the certificate thumbprint value from the provided .PFX certificate file. 
6. Encrypt the certificate password.
   * $encCertPswd = ConvertFrom-SecureString -SecureString $CertificatePassword -Key (1..16)
7. Add a PowerShell command in SetupComplete.cmd file to import certificate in \localmachine\MY (Local Computer ->       Personal) store on the new VM.
8. Run ‘Set-SILAggregator’ on Aggregator server to register certificate thumbprint from step 5 above.
9. Start and Configure SIL by adding two more commands in SetupComplete.cmd – 
   1. Set-SilLLogging
   1. Start-SilLogging
10. If changed, revert the TrustedHosts settings updated in step 2.



==============================
------------------------------
##4. Set-SILAPollingAccount
------------------------------
This function sets just enough permissions for a domain user on a target Hyper-V host server to be used as the SILA Polling Account for that host. This function adds the provided domain user account into the Remote Management Users group, Hyper-V administrators group and gives read only access to the root\CIMV2 namespace for SILA polling to work.  

Note: This does not add the host automatically to SILA polling operations.  That must be done separately.  This script can be executed in a loop to configure SIL on multiple computers (Windows Servers only).

===
####Example:

    $targetMachineCredential = Get-Credential
       
    Set-SILAPollingAccount -computername Contoso1 -domain Contosodomain -user existingDomainUser -targetMachineCredential $targetMachineCredential  
    
===
####Parameters:
| Parameter Name      | Type        | Required  | Description |
|:---|:---|:---|:---|
|computername|String|Y|Specifies a remote Hyper-V host to have a domain account added for SIL Aggregator polling.|	 
|domain|String|Y|Specifies the Active Directory domain for which the remote server and the domain account belong.|
|user|String|Y|Specifies the existing domain user to be added to the Hyper-V host for SIL Aggregator polling.|
|targetMachineCredential|PSCredential|N|Specifies the credentials to use to on the Hyper-V host to add the user and set the permissions|

Notes: 
 * To obtain a PSCredential object, use the ‘Get-Credential’ Cmdlet. For more information, type Get-Help Get-Credential.
 * For passwords use ConvertTo-SecureString Cmdlet.  Example: $pwd = ConvertTo-SecureString -String 'yourpassword' -AsPlainText -Force

===
----------------
References:
---------------

Software Inventory Logging Aggregator
https://technet.microsoft.com/en-us/library/mt572043.aspx

Manage Software Inventory Logging in Windows Server 2012 R2
https://technet.microsoft.com/en-us/library/dn383584.aspx

Software Inventory Logging Aggregator 1.0 for Windows Server
https://www.microsoft.com/en-us/download/details.aspx?id=49046

Add a Custom Script to Windows Setup
https://technet.microsoft.com/en-us/library/cc766314(v=ws.10).aspx

Run and RunOnce Registry Keys
https://msdn.microsoft.com/en-us/library/windows/desktop/aa376977(v=vs.85).aspx


