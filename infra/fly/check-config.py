#!/usr/bin/env python3
"""Offline check of infra/fly/*.fly.toml (ADR 0008). Python 3.11+, standard library only.

`flyctl config validate` needs a Fly.io access token, so CI cannot run it on pull requests. This script
parses both files, refuses unknown keys (typos), and checks the rules that the deployment depends on.

  python3 infra/fly/check-config.py                    # CI: the region may still be REGION-NOT-SET
  python3 infra/fly/check-config.py --require-region   # deploy: the region must be set, and the API must trust
                                                       # only the organization's /48 (not fdaa::/16)
  python3 infra/fly/check-config.py --require-region --first-deploy   # the first deploy: fdaa::/16 is allowed
  python3 infra/fly/check-config.py --print-apps       # prints "API_APP=..." and "WEB_APP=..." lines
"""

import ipaddress
import re
import sys
import tomllib
from pathlib import Path

ROOT = Path(__file__).resolve().parent
REGION_NOT_SET = "REGION-NOT-SET"
# Fly private networking (6PN). Each organization has one /48 inside it.
FLY_6PN = ipaddress.ip_network("fdaa::/16")
APP_NAME = re.compile(r"^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$")
REGION = re.compile(r"^[a-z]{3}$")
# Values of these names belong in `fly secrets set`, never in a committed file.
SECRET_NAME = re.compile(r"(ConnectionStrings|Secret|Password|Token|Dsn|AccessKey|ApiKey|ANON_KEY|SUPABASE_URL|HEADERS)", re.I)

ALLOWED = {
    "": {"app", "primary_region", "kill_signal", "kill_timeout", "deploy", "env", "http_service", "checks", "vm"},
    "deploy": {"strategy"},
    "http_service": {"internal_port", "force_https", "auto_stop_machines", "auto_start_machines",
                     "min_machines_running", "concurrency", "checks"},
    "http_service.concurrency": {"type", "soft_limit", "hard_limit"},
    "http_service.checks": {"method", "path", "interval", "timeout", "grace_period"},
    "checks.*": {"type", "port", "method", "path", "interval", "timeout", "grace_period"},
    "vm": {"size", "memory"},
}


def unknown_keys(config: dict, errors: list[str], name: str) -> None:
    def check(table: dict, section: str) -> None:
        allowed = ALLOWED[section]
        for key in table:
            if key not in allowed:
                errors.append(f"{name}: unknown key '{key}' in [{section or 'top level'}]")

    check(config, "")
    for section in ("deploy", "http_service", "http_service.concurrency"):
        table = config
        for part in section.split("."):
            table = table.get(part, {}) if isinstance(table, dict) else {}
        check(table, section)
    for item in config.get("http_service", {}).get("checks", []):
        check(item, "http_service.checks")
    for item in config.get("checks", {}).values():
        check(item, "checks.*")
    for item in config.get("vm", []):
        check(item, "vm")


def common(config: dict, errors: list[str], name: str) -> None:
    unknown_keys(config, errors, name)
    if not APP_NAME.fullmatch(config.get("app", "")):
        errors.append(f"{name}: app must be a Fly app name (lowercase letters, digits, '-')")
    region = config.get("primary_region", "")
    if region != REGION_NOT_SET and not REGION.fullmatch(region):
        errors.append(f"{name}: primary_region must be a Fly region code (for example fra) or {REGION_NOT_SET}")
    if config.get("kill_signal") != "SIGTERM":
        errors.append(f"{name}: kill_signal must be SIGTERM (graceful shutdown)")
    service = config.get("http_service", {})
    if service.get("internal_port") != 8080:
        errors.append(f"{name}: http_service.internal_port must be 8080 (the image port)")
    if service.get("auto_stop_machines") != "off" or service.get("min_machines_running", 0) < 1:
        errors.append(f"{name}: one machine must stay on (auto_stop_machines = \"off\", min_machines_running >= 1)")
    if not service.get("checks"):
        errors.append(f"{name}: http_service needs a health check (it gates the bluegreen deploy)")
    if len(config.get("vm", [])) != 1:
        errors.append(f"{name}: exactly one [[vm]] size")
    for key in config.get("env", {}):
        if SECRET_NAME.search(key):
            errors.append(f"{name}: [env] {key} belongs in `fly secrets set`, not in the file")


def check_api(api: dict, errors: list[str], deploy: bool, first_deploy: bool) -> None:
    name = "api.fly.toml"
    common(api, errors, name)
    if api.get("deploy", {}).get("strategy") != "bluegreen":
        errors.append(f"{name}: deploy.strategy must be \"bluegreen\" (zero downtime with one machine)")
    paths = [c.get("path") for c in api.get("http_service", {}).get("checks", [])]
    if paths != ["/health/ready"]:
        errors.append(f"{name}: the service check must be GET /health/ready")
    if [c.get("path") for c in api.get("checks", {}).values()] != ["/health/live"]:
        errors.append(f"{name}: the top-level check must be GET /health/live")
    if api.get("http_service", {}).get("force_https"):
        errors.append(f"{name}: force_https must be false (no public address; the web app calls it over 6PN)")
    networks = [v for k, v in api.get("env", {}).items() if k.startswith("ReverseProxy__KnownNetworks__")]
    if not networks:
        errors.append(f"{name}: ReverseProxy__KnownNetworks__0 must list the private network of the web app")
    for value in networks:
        try:
            network = ipaddress.ip_network(value)
        except ValueError:
            errors.append(f"{name}: {value} is not a CIDR network")
            continue
        if network.version != 6 or not network.subnet_of(FLY_6PN):
            errors.append(f"{name}: {value} must be inside the Fly private network {FLY_6PN}, never /0 (ADR 0006 #8)")
        elif deploy and not first_deploy and network.prefixlen < 48:
            errors.append(f"{name}: {value} trusts every Fly organization's prefix; set the organization /48 "
                          "(runbook, \"First deploy\"), or run the first deploy with first_deploy")
    if "ReverseProxy__TrustAnyHop" in api.get("env", {}):
        errors.append(f"{name}: ReverseProxy__TrustAnyHop must not be set")


def check_web(web: dict, api: dict, errors: list[str]) -> None:
    name = "web.fly.toml"
    common(web, errors, name)
    if not web.get("http_service", {}).get("force_https"):
        errors.append(f"{name}: force_https must be true")
    if web.get("env", {}).get("VYBE_API_HOST") != f"{api.get('app')}.internal":
        errors.append(f"{name}: VYBE_API_HOST must be \"{api.get('app')}.internal\" (the API app of api.fly.toml)")
    if web.get("primary_region") != api.get("primary_region"):
        errors.append("web.fly.toml and api.fly.toml must have the same primary_region")


def main(argv: list[str]) -> int:
    api = tomllib.loads((ROOT / "api.fly.toml").read_text(encoding="utf-8"))
    web = tomllib.loads((ROOT / "web.fly.toml").read_text(encoding="utf-8"))
    errors = []
    deploy = "--require-region" in argv
    check_api(api, errors, deploy, "--first-deploy" in argv)
    check_web(web, api, errors)
    if deploy and api.get("primary_region") == REGION_NOT_SET:
        errors.append(f"primary_region is {REGION_NOT_SET}: set the Supabase region in both fly.toml files first")
    if errors:
        for error in errors:
            print(f"error: {error}", file=sys.stderr)
        return 1
    if "--print-apps" in argv:
        print(f"API_APP={api['app']}")
        print(f"WEB_APP={web['app']}")
    else:
        print("infra/fly: api.fly.toml and web.fly.toml are valid")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
