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

using Microsoft.Azure.Management.Internal.Resources.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Azure.Management.Internal.Resources
{
    /// <summary>
    /// Operations for managing deployments.
    /// </summary>
    public partial interface IDeploymentOperations
    {
        /// <summary>
        /// Begin deleting deployment.To determine whether the operation has
        /// finished processing the request, call
        /// GetLongRunningOperationStatus.
        /// </summary>
        /// <param name='resourceGroupName'>
        /// The name of the resource group. The name is case insensitive.
        /// </param>
        /// <param name='deploymentName'>
        /// The name of the deployment to be deleted.
        /// </param>
        /// <param name='cancellationToken'>
        /// Cancellation token.
        /// </param>
        /// <returns>
        /// A standard service response for long running operations.
        /// </returns>
        Task<LongRunningOperationResponse> BeginDeletingAsync(string resourceGroupName, string deploymentName, CancellationToken cancellationToken);

        /// <summary>
        /// Cancel a currently running template deployment.
        /// </summary>
        /// <param name='resourceGroupName'>
        /// The name of the resource group. The name is case insensitive.
        /// </param>
        /// <param name='deploymentName'>
        /// The name of the deployment.
        /// </param>
        /// <param name='cancellationToken'>
        /// Cancellation token.
        /// </param>
        /// <returns>
        /// A standard service response including an HTTP status code and
        /// request ID.
        /// </returns>
        Task<AzureOperationResponse> CancelAsync(string resourceGroupName, string deploymentName, CancellationToken cancellationToken);

        /// <summary>
        /// Checks whether deployment exists.
        /// </summary>
        /// <param name='resourceGroupName'>
        /// The name of the resource group to check. The name is case
        /// insensitive.
        /// </param>
        /// <param name='deploymentName'>
        /// The name of the deployment.
        /// </param>
        /// <param name='cancellationToken'>
        /// Cancellation token.
        /// </param>
        /// <returns>
        /// Deployment information.
        /// </returns>
        Task<DeploymentExistsResult> CheckExistenceAsync(string resourceGroupName, string deploymentName, CancellationToken cancellationToken);

        /// <summary>
        /// Create a named template deployment using a template.
        /// </summary>
        /// <param name='resourceGroupName'>
        /// The name of the resource group. The name is case insensitive.
        /// </param>
        /// <param name='deploymentName'>
        /// The name of the deployment.
        /// </param>
        /// <param name='parameters'>
        /// Additional parameters supplied to the operation.
        /// </param>
        /// <param name='cancellationToken'>
        /// Cancellation token.
        /// </param>
        /// <returns>
        /// Template deployment operation create result.
        /// </returns>
        Task<DeploymentOperationsCreateResult> CreateOrUpdateAsync(string resourceGroupName, string deploymentName, Deployment parameters, CancellationToken cancellationToken);

        /// <summary>
        /// Delete deployment and all of its resources.
        /// </summary>
        /// <param name='resourceGroupName'>
        /// The name of the resource group. The name is case insensitive.
        /// </param>
        /// <param name='deploymentName'>
        /// The name of the deployment to be deleted.
        /// </param>
        /// <param name='cancellationToken'>
        /// Cancellation token.
        /// </param>
        /// <returns>
        /// A standard service response including an HTTP status code and
        /// request ID.
        /// </returns>
        Task<AzureOperationResponse> DeleteAsync(string resourceGroupName, string deploymentName, CancellationToken cancellationToken);

        /// <summary>
        /// Get a deployment.
        /// </summary>
        /// <param name='resourceGroupName'>
        /// The name of the resource group to get. The name is case insensitive.
        /// </param>
        /// <param name='deploymentName'>
        /// The name of the deployment.
        /// </param>
        /// <param name='cancellationToken'>
        /// Cancellation token.
        /// </param>
        /// <returns>
        /// Template deployment information.
        /// </returns>
        Task<DeploymentGetResult> GetAsync(string resourceGroupName, string deploymentName, CancellationToken cancellationToken);

        /// <summary>
        /// Get a list of deployments.
        /// </summary>
        /// <param name='resourceGroupName'>
        /// The name of the resource group to filter by. The name is case
        /// insensitive.
        /// </param>
        /// <param name='parameters'>
        /// Query parameters. If null is passed returns all deployments.
        /// </param>
        /// <param name='cancellationToken'>
        /// Cancellation token.
        /// </param>
        /// <returns>
        /// List of deployments.
        /// </returns>
        Task<DeploymentListResult> ListAsync(string resourceGroupName, DeploymentListParameters parameters, CancellationToken cancellationToken);

        /// <summary>
        /// Get a list of deployments.
        /// </summary>
        /// <param name='nextLink'>
        /// NextLink from the previous successful call to List operation.
        /// </param>
        /// <param name='cancellationToken'>
        /// Cancellation token.
        /// </param>
        /// <returns>
        /// List of deployments.
        /// </returns>
        Task<DeploymentListResult> ListNextAsync(string nextLink, CancellationToken cancellationToken);

        /// <summary>
        /// Validate a deployment template.
        /// </summary>
        /// <param name='resourceGroupName'>
        /// The name of the resource group. The name is case insensitive.
        /// </param>
        /// <param name='deploymentName'>
        /// The name of the deployment.
        /// </param>
        /// <param name='parameters'>
        /// Deployment to validate.
        /// </param>
        /// <param name='cancellationToken'>
        /// Cancellation token.
        /// </param>
        /// <returns>
        /// Information from validate template deployment response.
        /// </returns>
        Task<DeploymentValidateResponse> ValidateAsync(string resourceGroupName, string deploymentName, Deployment parameters, CancellationToken cancellationToken);
    }
}