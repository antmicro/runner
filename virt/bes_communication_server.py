from typing import Optional, Dict, List, Type
from io import SEEK_END
from os import environ, path, remove
from glob import glob
from logging import getLogger
from tempfile import TemporaryFile
from collections import defaultdict
from datetime import datetime, timedelta
from contextlib import asynccontextmanager
import asyncio
import json
try:
    from fastapi import FastAPI
    from fastapi.responses import JSONResponse
    from fastapi_utils.tasks import repeat_every
    from pydantic import BaseModel, validator, root_validator
    from distantbes import Invocation
    from distantbes.proto.proto import build_event_stream_pb2 as bes
    import requests
except ModuleNotFoundError:
    print("Missing requirements (check requirements.txt)")
    exit(2)

from vm_command import METADATA, get_metadata

BRV_COMMIT_STATUS_NAME = "build-results-viewer"
BRV_PUBLIC_URL = get_metadata(METADATA.BRV_PUBLIC_URL)
BRV_URL = get_metadata(METADATA.BRV_URL)
SOCKET = "/tmp/bes.socket"
RELOAD = True if environ.get("RELOAD") is not None else False
LOGGER = getLogger("uvicorn")
BACKUP_FILE = path.realpath(__file__) + ".backup"
GITHUB_TO_BES_STATUS = [1, 2, 4, 5, 5, 5]


### Types of messeges


class Identifier(BaseModel):
    id: str


class IdentifierWithRepoInfo(Identifier):
    repo: str
    sha: str
    run_id: str
    run_attempt: int


class InvocationID(Identifier):
    @validator("id")
    def _invocation_exists(cls, id):
        if id not in INVOCATIONS:
            raise ValueError("Missing invocation")
        return id


class Log(InvocationID):
    logs: Optional[str]
    log_file: Optional[str]

    @root_validator
    def _validate_fields(cls, values):
        logs, file = values.get("logs"), values.get("log_file")
        if logs is None and file is None:
            raise ValueError("Either 'logs' or 'log_file' has to be defined")
        return values

    def get_log(self) -> Optional[str]:
        if self.logs is not None:
            return self.logs
        if self.log_file is None:
            return None
        with open(self.log_file, "r") as fd:
            result = fd.read()
        return result


class Target(InvocationID):
    target_name: str


class TargetWithToken(Target):
    token: str


class DeleteTarget(Target):
    status: int = 1
    duration: int = 1000


class TargetLog(Target, Log):
    pass


class TargetArtifact(Target):
    artifact: str


class Response(BaseModel):
    error: Optional[str]
    id: str
    invocation_id: Optional[str]


### Utils


def create_response(_id: str) -> Response:
    response = Response(id=_id)
    if _id in INVOCATIONS:
        response.invocation_id = INVOCATIONS[_id].invocation_id
    return response


def create_error(_id: str, message: str) -> Response:
    return Response(id=_id, error=message)


def exception_wrapper(T: Type[Identifier]):
    def _decorator(func):
        async def wrapped_func(item: T) -> Response:
            try:
                await func(item)
                return create_response(item.id)
            except Exception as e:
                LOGGER.error(e)
                return create_error(item.id, str(e))

        return wrapped_func

    return _decorator


class TargetWrapper:
    def __init__(self, token: str):
        self.artifacts: List = []
        self.status: Optional[int] = None
        self.token: str = token
        self.duration: int = None
        self.tmp_logs_file = None

    def append_logs(self, logs: str):
        if self.tmp_logs_file is None:
            self.tmp_logs_file = TemporaryFile("w+")
        self.tmp_logs_file.seek(0, SEEK_END)
        self.tmp_logs_file.write(logs)

    def get_logs(self):
        if self.tmp_logs_file is None:
            return None
        self.tmp_logs_file.seek(0)
        return self.tmp_logs_file.read()

    def close_logs(self):
        if self.tmp_logs_file is not None:
            self.tmp_logs_file.close()
            self.tmp_logs_file = None


class InvocationWrapper(Invocation):
    def __init__(self, repo: str, sha: str, run_id: str, run_attempt: int, uuid: str = None):
        super().__init__(grpc_bes_url=BRV_URL, uuid=uuid)
        self.targets_quantity: Optional[int] = None
        self.targets_wrapped: Dict[str, TargetWrapper] = dict()
        self.run_info: str = ""
        self.creation_time = datetime.now()
        self.lock = asyncio.Lock()

        self.repo: str = repo
        self.sha: str = sha
        self.run_id: str = run_id
        self.run_attempt: int = run_attempt

        self.open()

    def _create_header(self, target_name: str):
        return {
            "Authorization": f"Bearer {self.targets_wrapped[target_name].token}",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
        }

    def get_jobs(self, target: str, page: int = 1, per_page: int = 1) -> Dict:
        response = requests.get(
            f"https://api.github.com/repos/{self.repo}/actions/runs/{self.run_id}/jobs?per_page={per_page}&page={page}",
            headers=self._create_header(target),
        )
        response.raise_for_status()
        return response.json()

    def get_last_commit_status(self, target: str) -> Optional[Dict]:
        response = requests.get(
            f"https://api.github.com/repos/{self.repo}/statuses/{self.sha}",
            headers=self._create_header(target),
        )
        if response.status_code != 200 or len(response.json()) == 0:
            return None
        return max(
            filter(lambda x: x["context"] == BRV_COMMIT_STATUS_NAME, response.json()),
            key=lambda x: x["updated_at"]
        )

    def create_commit_status(self, target: str, success: Optional[bool] = None):
        if BRV_PUBLIC_URL == "":
            LOGGER.info("Public URL of BRV is not available -- commit status won't be created.")
            return

        last_commit_status = self.get_last_commit_status(target)
        if last_commit_status is not None:
            msg = f"\nResults of previous attempt: {last_commit_status['target_url']}"
            self.add_stdout(msg)
            self.run_info += msg + '\n'
            self.flush()

        body = {
            "state": "pending"
            if success is None
            else ("success" if success else "failure"),
            "target_url": f"{BRV_PUBLIC_URL}{'' if BRV_PUBLIC_URL.endswith('/') else '/'}results/invocations/{self.invocation_id}",
            "context": BRV_COMMIT_STATUS_NAME,
        }
        response = requests.post(
            f"https://api.github.com/repos/{self.repo}/statuses/{self.sha}",
            headers=self._create_header(target),
            json=body,
        )
        response.raise_for_status()

    def add_target(self, name: str, token: str):
        if name not in self.targets_wrapped:
            self.announce_target(name)
            self.targets_wrapped[name] = TargetWrapper(token)
            self.flush()
        if self.targets_quantity is None:
            self.targets_quantity = self._get_unfinished_jobs(name)[-1]
            self.create_commit_status(name)

    def add_run_information(self, info: str):
        if self.run_info == "":
            self.add_stdout(info)
            self.flush()
            self.run_info += info + '\n'

    def add_artifacts(self, target: str, artifacts: str):
        LOGGER.info(f"Artifacts schema: {artifacts}")
        for artifact in glob(artifacts, recursive=True):
            work_dir = path.commonpath([artifacts, artifact])
            if path.isdir(artifact):
                continue
            LOGGER.info(f"Found artifact: {artifact}")
            with open(artifact, "rb") as fd:
                uploaded = self.cas_uploader.put_blob(
                    fd.read(), artifact[len(work_dir) + 1 :]
                )
            self.targets_wrapped[target].artifacts.append(uploaded)

    def add_target_logs(self, target: str, log: str):
        self.targets_wrapped[target].append_logs(log)

    def all_targets_finished(self) -> bool:
        return not any(
            [target.status is None for target in self.targets_wrapped.values()]
        )

    def delete_target(self, target: str, status: int, duration: int) -> bool:
        self.add_test_to_target(
            target,
            GITHUB_TO_BES_STATUS[status],
            logstr=self.targets_wrapped[target].get_logs(),
            duration=duration,
            total_duration=duration,
        )
        self.finalize_target(
            target, status == 0, self.targets_wrapped[target].artifacts
        )
        self.targets_wrapped[target].status = status
        self.targets_wrapped[target].duration = duration
        self.flush()

        return self.delete(target)

    def _get_unfinished_jobs(self, target: str, early_stop: bool = False) -> int:
        page = 1
        run = self.get_jobs(target, per_page=100, page=1)
        total_count = run["total_count"] - 100
        all_jobs = len(run["jobs"])
        canncelled_jobs = len(
            [job for job in run["jobs"]
                if job["conclusion"] is not None and 
                job["conclusion"] not in ("success", "failure")]
        )
        unfinished_jobs = len(
            [job for job in run["jobs"] if job["status"] != "completed"]
        )
        while (not early_stop or unfinished_jobs < 2) and total_count > 0:
            page += 1
            jobs = self.get_jobs(target, per_page=100, page=page)["jobs"]
            all_jobs += len(jobs)
            canncelled_jobs += len(
                [job for job in jobs
                    if job["conclusion"] is not None and 
                    job["conclusion"] not in ("success", "failure")]
            )
            unfinished_jobs += len(
                [
                    job
                    for job in jobs if job["status"] != "completed"
                ]
            )
            total_count -= 100
        LOGGER.info(f"Found {unfinished_jobs} unfinished jobs")
        return unfinished_jobs, canncelled_jobs, all_jobs

    def _delete(self, target: str, set_commit_status: bool = False):
        [t.close_logs() for t in self.targets_wrapped.values()]
        success = all((t.status == 0 for t in self.targets_wrapped.values()))
        self.close(0 if success else 1)
        if set_commit_status:
            self.create_commit_status(target, success)

    def delete(self, target: str) -> bool:
        all_target_finished = self.all_targets_finished()
        if all_target_finished:
            unfinished_jobs, cancelled_jobs, all_jobs = \
                self._get_unfinished_jobs(target)
            if (
                all_jobs == len(self.targets_wrapped)
                or all_jobs - cancelled_jobs <= len(self.targets_wrapped)
            ):
                self._delete(target, True)
                return True
        return False

    def serialize(self) -> Dict:
        self.flush()
        serialized = {
            "invocation_id": self.invocation_id,
            "repo": self.repo,
            "sha": self.sha,
            "run_id": self.run_id,
            "run_attempt": self.run_attempt,
            "run_info": self.run_info,
            "creation_time": self.creation_time.isoformat(),
            "targets_quantity": self.targets_quantity,
            "targets_wrapped": {k: {
                "status": t.status,
                "artifacts": [{
                    "name": art.name,
                    "uri": art.uri,
                } for art in t.artifacts],
                "token": t.token,
                "duration": t.duration,
                "logs": t.get_logs()
            } for k, t in self.targets_wrapped.items()}
        }
        [target.close_logs() for target in self.targets_wrapped.values()]
        return serialized

    @classmethod
    def deserialize(cls, item: Dict):
        inv: InvocationWrapper = cls(item["repo"], item["sha"], item["run_id"], item["run_attempt"], item["invocation_id"])
        inv.run_info = item["run_info"]
        inv.add_stdout(inv.run_info, False)
        inv.creation_time = datetime.fromisoformat(item["creation_time"])
        inv.targets_quantity = item["targets_quantity"]
        for k, t in item["targets_wrapped"].items():
            tar = TargetWrapper(t["token"])
            tar.status = t["status"]
            tar.duration = t["duration"]
            tar.artifacts = [bes.File(name=art["name"], uri=art["uri"]) 
                for art in t["artifacts"]]
            inv.announce_target(k)
            if tar.status is not None:
                inv.add_test_to_target(
                    k,
                    GITHUB_TO_BES_STATUS[tar.status],
                    tar.duration,
                    tar.duration,
                    t["logs"])
                inv.finalize_target(k, tar.status == 0, tar.artifacts)
            if t["logs"] is not None:
                tar.append_logs(t["logs"])
            inv.targets_wrapped[k] = tar
        inv.flush()
        return inv


def close_old_invocation():
    ids_to_remove = []
    now = datetime.now()
    LOGGER.info(f"{now} Cleaning old invocations")
    for _id, invocation in INVOCATIONS.items():
        if (
            not invocation.all_targets_finished()
            or now - invocation.creation_time < timedelta(days=1)
        ):
            continue
        LOGGER.info(f"Invocation with id {_id} ({invocation.invocation_id}) is too old, closing...")
        invocation._delete()
        ids_to_remove.append(_id)
    for _id in ids_to_remove:
        del INVOCATIONS[_id]


INVOCATIONS: Dict[str, InvocationWrapper]
create_invocation_lock = asyncio.Lock()

@asynccontextmanager
async def lifespan(app: FastAPI):
    global INVOCATIONS
    if path.exists(BACKUP_FILE):
        print("Backup file found")
        with open(BACKUP_FILE, "r") as fd:
            _invs = json.load(fd)
            INVOCATIONS = {k: InvocationWrapper.deserialize(inv) for k, inv in _invs.items()}
        remove(BACKUP_FILE)
        del _invs
    else:
        print("Starting server without Invocations")
        INVOCATIONS = dict()

    await repeat_every(seconds=60 * 10, wait_first=True, logger=LOGGER)(close_old_invocation)()
    yield

    if len(INVOCATIONS) > 0:
        print("Creating backup file...")
        with open(BACKUP_FILE, "w") as fd:
            json.dump({k: inv.serialize() for k, inv in INVOCATIONS.items()}, fd)


### Endpoints

app = FastAPI(lifespan=lifespan)


@app.post("/invocation")
@exception_wrapper(IdentifierWithRepoInfo)
async def create_invocation(item: IdentifierWithRepoInfo):
    await create_invocation_lock.acquire()
    if item.id not in INVOCATIONS:
        INVOCATIONS[item.id] = InvocationWrapper(
            item.repo, item.sha, item.run_id, item.run_attempt
        )
    create_invocation_lock.release()


@app.post("/invocation/logs")
@exception_wrapper(Log)
async def set_output(item: Log):
    text = item.get_log()
    await INVOCATIONS[item.id].lock.acquire()
    INVOCATIONS[item.id].add_run_information(text)
    INVOCATIONS[item.id].lock.release()


@app.post("/invocation/target")
@exception_wrapper(TargetWithToken)
async def create_target(target: TargetWithToken):
    await INVOCATIONS[target.id].lock.acquire()
    INVOCATIONS[target.id].add_target(target.target_name, target.token)
    INVOCATIONS[target.id].lock.release()


@app.post("/invocation/target/log")
@exception_wrapper(TargetLog)
async def add_target_log(target: TargetLog):
    await INVOCATIONS[target.id].lock.acquire()
    INVOCATIONS[target.id].add_target_logs(target.target_name, target.get_log())
    INVOCATIONS[target.id].lock.release()


@app.post("/invocation/target/artifact")
@exception_wrapper(TargetArtifact)
async def add_artifact(target: TargetArtifact):
    await INVOCATIONS[target.id].lock.acquire()
    INVOCATIONS[target.id].add_artifacts(target.target_name, target.artifact)
    INVOCATIONS[target.id].lock.release()


@app.delete("/invocation/target")
@exception_wrapper(DeleteTarget)
async def delete_target(target: DeleteTarget):
    await INVOCATIONS[target.id].lock.acquire()
    delete = INVOCATIONS[target.id].delete_target(
        target.target_name, target.status, target.duration
    )
    INVOCATIONS[target.id].lock.release()
    if delete:
        del INVOCATIONS[target.id]


if __name__ == "__main__":
    import uvicorn
    from uvicorn.config import LOGGING_CONFIG

    if BRV_URL == "":
        LOGGER.info("BRV URL has not been specified, exiting")
        exit(1)
    logging_config = LOGGING_CONFIG.copy()
    logging_config["formatters"]["access"]["fmt"] = "%(levelprefix)s %(asctime)s %(message)s"
    uvicorn.run("__main__:app", uds=SOCKET, reload=RELOAD)
