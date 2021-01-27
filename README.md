# GitHub Actions Runner

This repository contains the code of [GitHub Actions Runner](https://github.com/actions/runner.git) modified to spawn QEMU instances with Singularity containers and to perform run steps within them.

## Installation

This software has been tested to work on Debian Buster and Bullseye.

As a prerequisite step, please run `sudo dpkg --set-selections < requirements.dpkg && sudo apt-get dselect-upgrade` to install most of the required dependencies.

The installation of QEMU was intentionally left out from the aforementioned command.
This is because you may want to build QEMU from sources or install a custom package.
If not, simply running `sudo apt -qqy install qemu-system-x86` will suffice.

After grabbing all runtime dependencies, run `cd src && ./dev.sh layout Debug && cd -`.
This will build the runner software from sources.
After the compilation has finished, the resulting binaries can be found in the `_layout` directory.

The recommended way of running this software in the production is by using the systemd units which first have to be generated and installed.
In order to do that, run `./install_systemd_services.sh`.
Please be advised that this will assume absolute paths to this repository so if you ever decide to move it elsewhere, make sure to run the script again.

> WARNING: You may have to stop and disable dnsmasq systemd unit to avoid conflicts by running `sudo systemctl stop dnsmasq && sudo systemctl disable dnsmasq`

The next step is defining the parameters of virtual machines which will be spawned.
Simply copy the `.vm_specs.example` to `.vm_specs` and adjust the parameters accordingly.

The last step is registering the runner in a repository.
To do this, run `./config.sh --url https://github.com/$REPOSITORY_ORG/$REPOSITORY_NAME --token $TOKEN --num $SLOTS` with `$TOKEN` being the registration token found in the **Actions** tab in the repository settings and `$SLOTS` being the number of runner slots to allocate.

## Starting the runner

### Manual method

In order to start the runner manually, first start the `tap.sh` script providing the number of interfaces to create, e.g. `./tap.sh 4`.
This script is blocking so it's advised to run it in a terminal multiplexer.

Next, run `SCALE=<number of slots> supervisord -n -c supervisord.conf`.

### systemd

First set up networking by running `sudo systemctl start gha-taps@$SLOTS` replacing `$SLOTS` with the number of interfaces you'd like to create.

Then, start the runner by running `sudo systemctl start gha-main@$SLOTS` replacing `$SLOTS` with the number of runner slots you'd like to allocate.
