#
#  Copyright (c) Microsoft Corporation.
#  Licensed under the Apache License, Version 2.0 (the "License");
#  you may not use this file except in compliance with the License.
#  You may obtain a copy of the License at
#  https://www.apache.org/licenses/LICENSE-2.0
#
#  Unless required by applicable law or agreed to in writing, software
#  distributed under the License is distributed on an "AS IS" BASIS,
#  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
#  See the License for the specific language governing permissions and
#  limitations under the License.
#
# ------------------ PackageManagement Test  -----------------------------------

$InternalGallery = "https://www.poshtestgallery.com/api/v2/"
$InternalSource = 'OneGetTestSource'

Describe "PackageManagement Acceptance Test" -Tags "Feature" {

 BeforeAll{
    Register-PackageSource -Name Nugettest -provider NuGet -Location https://www.nuget.org/api/v2 -Force
    Register-PackageSource -Name $InternalSource -Location $InternalGallery -ProviderName 'PowerShellGet' -Trusted -ErrorAction SilentlyContinue
    $SavedProgressPreference = $ProgressPreference
    $ProgressPreference = "SilentlyContinue"
 }
 AfterAll {
     $ProgressPreference = $SavedProgressPreference
 }
    It "get-packageprovider" {

        $gpp = Get-PackageProvider

        $gpp.Name | Should -Contain 'NuGet'

        $gpp.Name | Should -Contain 'PowerShellGet'
    }

    It "find-packageprovider PowerShellGet" {
        $fpp = (Find-PackageProvider -Name "PowerShellGet" -Force).name
        $fpp | Should -Contain "PowerShellGet"
    }

     It "install-packageprovider, Expect succeed" {
        $ipp = (Install-PackageProvider -Name gistprovider -Force -Source $InternalSource -Scope CurrentUser).name
        $ipp | Should -Contain "gistprovider"
    }

    It "Find-package"  {
        $f = Find-Package -ProviderName NuGet -Name jquery -Source Nugettest
        $f.Name | Should -Contain "jquery"
	}

    It "Install-package"  {
        $i = Install-Package -ProviderName NuGet -Name jquery -Force -Source Nugettest -Scope CurrentUser
        $i.Name | Should -Contain "jquery"
	}

    It "Get-package"  {
        $g = Get-Package -ProviderName NuGet -Name jquery
        $g.Name | Should -Contain "jquery"
	}

    It "save-package"  {
        $s = Save-Package -ProviderName NuGet -Name jquery -Path $TestDrive -Force -Source Nugettest
        $s.Name | Should -Contain "jquery"
	}

    It "uninstall-package"  {
        $u = Uninstall-Package -ProviderName NuGet -Name jquery
        $u.Name | Should -Contain "jquery"
	}
}
