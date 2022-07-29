# GitHub Actions Runner

This repository contains the code of [GitHub Actions Runner](https://github.com/actions/runner.git) modified to spawn preemptible GCP instances with Singularity containers and to perform run steps within them.

## Description

The software was designed to run in [Google Compute Engine](https://cloud.google.com/compute).
Therefore, it is necessary to prepare some virtual infrastructure prior to installing the runner.

The repositories listed below contain the definitions of the required components:

* [github-actions-runner-scalerunner](https://github.com/antmicro/github-actions-runner-scalerunner) - the image used by preemptible GCP instances that serve as workers (one worker per job).
* [github-actions-runner-terraform](https://github.com/antmicro/github-actions-runner-terraform) - a [Terraform](https://www.terraform.io/) module used to create the virtual network, firewall rules, cloud NAT and coordinator instance for the runner.

## Installation and configuration

The manual below assumes that Debian Buster is used to deploy the runner.

### Host prerequisites

The following packages must be installed:

* `build-essential`
* [Terraform](https://www.terraform.io/docs/cli/install/apt.html)
* [Google Cloud SDK](https://cloud.google.com/sdk/docs/install#deb)

### Installation steps

With all prerequisites in place, in order to install the software, follow the steps below:

Install the [Google Cloud SDK](https://cloud.google.com/sdk/docs/install#deb) and setup the project:

```bash
# Authenticate with GCP.
gcloud auth login

# Create a GCP project for your runner.
export PROJECT=example-runner-project
gcloud projects create $PROJECT
gcloud config set project $PROJECT

# At this point, billing needs to be enabled.
# To do this, follow the instructions from the link below:
# https://cloud.google.com/billing/docs/how-to/modify-project

# Enable the necessary APIs in your project.
gcloud services enable compute.googleapis.com
gcloud services enable storage-component.googleapis.com
gcloud services enable storage.googleapis.com
gcloud services enable storage-api.googleapis.com

# Create and setup a service account.
export SERVICE_ACCOUNT_ID=runner-manager

gcloud iam service-accounts create $SERVICE_ACCOUNT_ID

export FULL_SA_MAIL=$SERVICE_ACCOUNT_ID@$PROJECT.iam.gserviceaccount.com

gcloud projects add-iam-policy-binding $PROJECT \
    --member="serviceAccount:$FULL_SA_MAIL" \
    --role="roles/compute.admin"

gcloud projects add-iam-policy-binding $PROJECT \
    --member="serviceAccount:$FULL_SA_MAIL" \
    --role="roles/iam.serviceAccountCreator"

gcloud projects add-iam-policy-binding $PROJECT \
    --member="serviceAccount:$FULL_SA_MAIL" \
    --role="roles/iam.serviceAccountUser"

gcloud projects add-iam-policy-binding $PROJECT \
    --member="serviceAccount:$FULL_SA_MAIL" \
    --role="roles/iam.serviceAccountKeyAdmin"

gcloud projects add-iam-policy-binding $PROJECT \
    --member="serviceAccount:$FULL_SA_MAIL" \
    --role="roles/resourcemanager.projectIamAdmin"

# Create and download SA key.
# WARNING: the export below will be used by Terraform later.
export GOOGLE_APPLICATION_CREDENTIALS=$HOME/$SERVICE_ACCOUNT_ID.json
gcloud iam service-accounts keys create $GOOGLE_APPLICATION_CREDENTIALS \
    --iam-account=$FULL_SA_MAIL

# Create a GCP bucket for worker image.
export BUCKET=$PROJECT-worker-bucket
gsutil mb gs://$BUCKET
```

Build and upload the worker image:

```bash
# Clone the repository
git clone https://github.com/antmicro/github-actions-runner-scalerunner.git
cd github-actions-runner-scalerunner

# Compile bzImage
cd buildroot && make BR2_EXTERNAL=../overlay/ scalenode_gcp_defconfig && make

# Prepare a disk for GCP
./make_gcp_image.sh

# Upload the resulting tar archive
./upload_gcp_image.sh $PROJECT $BUCKET
```

Setup virtual infrastructure using Terraform:

```bash
git clone https://github.com/antmicro/github-actions-runner-terraform.git
terraform init && terraform apply
```

Connect to the coordinator instance created in the previous step:

```bash
gcloud compute ssh <COORDINATOR_INSTANCE> --zone <COORDINATOR_ZONE> 
```

Install and configure the runner on the coordinator instance according to the instructions below.
The registration token (the `$TOKEN` variable) can be obtained from the **Runners settings** page in repository settings (`https://github.com/$REPOSITORY_ORG/$REPOSITORY_NAME/settings/actions/runners/new`) or using the [Self-hosted runners API](https://docs.github.com/en/rest/reference/actions#create-a-registration-token-for-an-organization). 

```bash
# Update repositories and install wget.
sudo apt -qqy update && sudo apt -qqy install wget

# Download and run the installation script.
wget -O - https://raw.githubusercontent.com/antmicro/runner/vm-runners/scripts/install.sh | sudo bash

# The runner software runs as the 'runner' user, so let's sudo into it.
sudo -i -u runner
cd /home/runner/github-actions-runner

# Init and update submodules
git submodule update --init --recursive

# Copy the .vm_specs.json file and adjust the parameters accordingly.
cp .vm_specs.example.json .vm_specs.json
vim .vm_specs.json

# Register the runner in the desired repository.
./config.sh --url https://github.com/$REPOSITORY_ORG/$REPOSITORY_NAME --token $TOKEN --num $SLOTS
```

### Multi-zone support

The default behavior for coordinator is to spawn worker machines in its own zone
(which is configured using the [gcp_zone](https://github.com/antmicro/github-actions-runner-terraform#input_gcp_zone) parameter).
However, certain workloads may trigger the `ZONE_RESOURCE_POOL_EXHAUSTED` error which is caused by a physical lack of available resources within a certain zone
(see the [support page](https://cloud.google.com/compute/docs/troubleshooting/troubleshooting-vm-creation) for more details).

If such error should occur, the software will make attempts to spawn the machine in neighboring zones within the region.
This behavior can be further expanded by defining a list of additional regions (see the [gcp_auxiliary_zones](https://github.com/antmicro/github-actions-runner-terraform#input_gcp_auxiliary_zones) parameter).

> WARNING: read on if you're planning to use the external disk feature.

For external disks to work in this arrangement, it is necessary to manually replicate them in all zones within the home region (and auxiliary regions if applicable).
Otherwise, jobs requiring an external disk will be constrained to zones where the disk and its replicas can be found.

Consider the example of replicating a balanced persistent disk called `auxdisk` located in `europe-west4-a` to `europe-west4-b`.

```bash
# Create a snapshot of the disk located in the home zone.
gcloud compute snapshots create auxdisk-snapshot-1 \
	--source-disk auxdisk \
	--source-disk-zone europe-west4-a

# Create a disk from the snapshot in another zone.
# Notice that we cannot assign the same name to it.
# We'll associate it by specifying the original name in the "gha-replica-for" label instead.
gcloud compute disks create another-auxdisk \
	--zone europe-west4-b \
	--labels gha-replica-for=auxdisk \
	--source-snapshot auxdisk-snapshot-1
```

It is possible to check the availability of a disk by running `python3 vm_command.py --mode get_disks -d auxdisk` on the coordinator machine 
(replacing the value for the `-d` argument with the name of the disk to check).
Example output of such an invocation might look as follows:

```console
runner@foo-runner:~/github-actions-runner/virt$ python3 vm_command.py --mode get_disks -d auxdisk
{'europe-west4-a': {'autoDelete': 'false', 'deviceName': 'aux', 'mode': 'READ_ONLY', 'source': 'projects/foo/zones/europe-west4-a/disks/auxdisk'}, 'europe-west4-b': {'autoDelete': 'false', 'deviceName': 'aux', 'mode': 'READ_ONLY', 'source': 'projects/foo/zones/europe-west4-b/disks/another-auxdisk'}}

```

### Delegate logging to an external Compute Engine disk (optional)

By default, timestamped runner logs are stored in `*_diag` directories under `$THIS_REPO_PATH/_layout`.

It is possible, however, to point the runner to store logs on an external disk.

A helper script is available which creates, formats and mounts such a disk.
In order to ensure persistence, a corresponding entry will be added to `/etc/fstab`.

To enable this feature, simply run `./scripts/setup_ext_log_disk.sh`.

After completing this step, restart the runner and the new mount point (`/var/log/runner`) will be picked up automatically.

### Stale VMs remover (optional)

To make sure there aren't any stale long-running runner VMs, it is possible to enable a cron job that automatically removes
any auto-spawned instances running for more than 12h.

To enable this feature, simply run `./scripts/install_stale_vm_remover.sh`.

### Enable logs compression and rotation (optional)

By default logs are stored 10 days until they are deleted.

It is possible to enable a cron job that compresses all log files that are at least 2 days old and removes old archives until there is enough free disc space.

To enable this feature, simply run `./scripts/install_compress_log_files_cron.sh`.

After completing this step, logs will be automatically compressed every day at 3 AM.

## Special variables

Certain environment variables set at the global level influence the VM initialization step.
By convention, they are prefixed with `GHA_`.

The table below documents and describes their purpose.

|         Environment variable        |      Type     |                                                             Description                                                            |
|:-----------------------------------:|:-------------:|:----------------------------------------------------------------------------------------------------------------------------------:|
| `GHA_EXTERNAL_DISK`                 | string        | Name of an external Compute Engine disk                                                                                            |
| `GHA_PREEMPTIBLE`                   | bool          | Set whether the machine should be preemptible.                                                                                     |
| `GHA_MACHINE_TYPE`                  | string        | Compute Engine machine type                                                                                                        |
| `GHA_SA`                            | string        | Machine service account suffix                                                                                                     |
| `GHA_SSH_TUNNEL_CONFIG`             | base64 string | OpenSSH configuration file for tunneling                                                                                           |
| `GHA_SSH_TUNNEL_KEY`                | base64 string | OpenSSH private key file                                                                                                           |
| `GHA_SSH_TUNNEL_CONFIG_SECRET_NAME` | string        | Secret name from [GCP Secret Manager](https://cloud.google.com/secret-manager) containing OpenSSH configuration file for tunneling |
| `GHA_SSH_TUNNEL_KEY_SECRET_NAME`    | string        | Secret name from [GCP Secret Manager](https://cloud.google.com/secret-manager) containing OpenSSH private key file                 |

### SSH port forwarding

It is possible to establish a secure tunnel to an SSH-enabled host in order to forward some ports.

First, prepare a configuration file according to the [OpenSSH client configuration syntax](https://linux.die.net/man/5/ssh_config).

An example configuration file may looks as follows:

```sshconfig
Host some-host
  HostName example.com
  User test
  StrictHostKeyChecking no
  ExitOnForwardFailure yes
  LocalForward localhost:8080 127.0.0.1:80
```

This will forward the HTTP port from `example.com` to port 8080 on the worker machine.
The forwarded port will be available within the job container (this will allow you to, for example, run `wget localhost:8080`).

Apart from preparing the configuration file, it is necessary to prepare a private key for authentication with the remote host,

There are two ways of exposing these files:
* Encode both files in Base64 (this can be done by running `cat <filename> | base64 -w0`), store them in [GitHub Actions Encrypted secrets](https://docs.github.com/en/actions/security-guides/encrypted-secrets) and expose them in the workflow file.
* Store them as secrets in [GCP Secret Manager](https://cloud.google.com/secret-manager) with two labels (`gha_runner_exposed: 1` and `gha_runner_namespace: $REPOSITORY_NAME`) and reference their names in the workflow file.

In the event that both methods are used in the workflow file, [Secret Manager](https://cloud.google.com/secret-manager) takes precedence.

An example workflow file leveraging this feature may look as follows:

```yaml
on: [push]

name: test

jobs:
  centos:
    container: centos:7
    runs-on: [self-hosted, Linux, X64]
    env:
      GHA_SSH_TUNNEL_KEY: "${{ secrets.GHA_SSH_TUNNEL_KEY }}"
      GHA_SSH_TUNNEL_CONFIG: "${{ secrets.GHA_SSH_TUNNEL_CONFIG }}"
      GHA_SSH_TUNNEL_CONFIG_SECRET_NAME: "my_tunnel_config"
      GHA_SSH_TUNNEL_KEY_SECRET_NAME: "my_tunnel_key"
    steps:
    - run: yum -y install wget
    - run: wget http://localhost:8080 && cat index.html
```

## Starting the runner

### Manual method

In order to start the runners manually, run `SCALE=<number of slots> supervisord -n -c supervisord.conf`.

### systemd

Start the runner by running `sudo systemctl start gha-main@$SLOTS` replacing `$SLOTS` with the number of runner slots you'd like to allocate.

If you want the software to start automatically, run the command above with the `enable` action instead of `start`.
