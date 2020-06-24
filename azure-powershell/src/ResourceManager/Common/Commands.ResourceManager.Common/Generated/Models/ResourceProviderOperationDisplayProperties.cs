// 
// Copyright (c) Microsoft and contributors.  All rights reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//   http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// 
// See the License for the specific language governing permissions and
// limitations under the License.
// 

// Warning: This code was generated by a tool.
// 
// Changes to this file may cause incorrect behavior and will be lost if the
// code is regenerated.


namespace Microsoft.Azure.Management.Internal.Resources.Models
{
    /// <summary>
    /// Resource provider operation's display properties.
    /// </summary>
    public partial class ResourceProviderOperationDisplayProperties
    {
        private string _description;

        /// <summary>
        /// Optional. Gets or sets operation description.
        /// </summary>
        public string Description
        {
            get { return this._description; }
            set { this._description = value; }
        }

        private string _operation;

        /// <summary>
        /// Optional. Gets or sets operation.
        /// </summary>
        public string Operation
        {
            get { return this._operation; }
            set { this._operation = value; }
        }

        private string _provider;

        /// <summary>
        /// Optional. Gets or sets operation provider.
        /// </summary>
        public string Provider
        {
            get { return this._provider; }
            set { this._provider = value; }
        }

        private string _publisher;

        /// <summary>
        /// Optional. Gets or sets operation description.
        /// </summary>
        public string Publisher
        {
            get { return this._publisher; }
            set { this._publisher = value; }
        }

        private string _resource;

        /// <summary>
        /// Optional. Gets or sets operation resource.
        /// </summary>
        public string Resource
        {
            get { return this._resource; }
            set { this._resource = value; }
        }

        /// <summary>
        /// Initializes a new instance of the
        /// ResourceProviderOperationDisplayProperties class.
        /// </summary>
        public ResourceProviderOperationDisplayProperties()
        {
        }
    }
}