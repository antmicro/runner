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

The last (albeit optional) step of this procedure is installing the systemd units.
In order to do that, run `./install_systemd_services.sh`.
Please be advised that this will assume absolute paths to this repository so if you ever decide to move it elsewhere, make sure to run the script again.

## Starting the runner

### Manual method

In order to start the runner manually, first start the `tap.sh` script providing the number of interfaces to create, e.g. `./tap.sh 4`.
This script is blocking so it's advised to run it in a terminal multiplexer.

Next, run `SCALE=<number of slots> supervisord -n -c supervisord.conf`.

### systemd

First set up networking by running `sudo systemctl start gha-taps@$N` replacing `$N` with the number of interfaces you'd like to create.

Then, start the runner by running `sudo systemctl start gha-main@$N` replacing `$N` with the number of runner slots you'd like to allocate.
