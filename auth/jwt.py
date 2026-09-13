#!/usr/bin/env python3
"""Local test-token wrapper around .NET 10 SDK user-jwts. Not an identity server."""
from __future__ import annotations

import argparse
import base64
import hashlib
import hmac
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import uuid

HOME = Path(__file__).resolve().parent / '.local'
PROJECT = HOME / 'issuer' / 'TestTokens.csproj'
ISSUER = 'motiva-benchmark'
AUDIENCE = 'motiva-api'
COMPANY = '11111111-1111-4111-8111-111111111111'
OTHER_COMPANY = '22222222-2222-4222-8222-222222222222'
TOKEN_RE = re.compile(r'(?<![\w-])eyJ[\w-]*\.[\w-]+\.[\w-]+(?![\w-])')


def private_write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    flags = os.O_WRONLY | os.O_CREAT | os.O_TRUNC
    fd = os.open(path, flags, 0o600)
    with os.fdopen(fd, 'w', encoding='utf-8', newline='\n') as file:
        file.write(text)
    if os.name != 'nt':
        path.chmod(0o600)


def run_dotnet(*args: str) -> str:
    env = {**os.environ, 'DOTNET_CLI_UI_LANGUAGE': 'en-US',
           'DOTNET_NOLOGO': 'true', 'DOTNET_CLI_TELEMETRY_OPTOUT': '1'}
    result = subprocess.run(['dotnet', *args], cwd=PROJECT.parent,
                            capture_output=True, text=True, env=env, timeout=120)
    if result.returncode:
        # Never dump CLI output: some commands contain tokens/signing material.
        raise RuntimeError('dotnet command failed (exit %s). Check SDK 10, project '
                           'and local user-secrets permissions. Output withheld '
                           'to avoid exposing credentials.' % result.returncode)
    return result.stdout


def ensure_project() -> None:
    if not shutil.which('dotnet'):
        raise RuntimeError('Install/select .NET 10 SDK; dotnet was not found.')
    PROJECT.parent.mkdir(parents=True, exist_ok=True)
    if os.name != 'nt':
        HOME.chmod(0o700)
    # Pin the utility to a locally installed SDK 10. No network or package restore.
    sdks = subprocess.run(['dotnet', '--list-sdks'], capture_output=True,
                          text=True, check=True, timeout=30).stdout
    versions = re.findall(r'^(10\.\d+\.\d+)\s', sdks, re.MULTILINE)
    if not versions:
        raise RuntimeError('A stable .NET 10 SDK is required; none was found.')
    global_json = PROJECT.parent / 'global.json'
    if not global_json.exists():
        version = max(versions, key=lambda value: tuple(map(int, value.split('.'))))
        private_write(global_json, json.dumps({'sdk': {'version': version,
                       'rollForward': 'disable'}}, indent=2) + '\n')
    if not PROJECT.exists():
        secrets_id = 'motiva-test-' + uuid.uuid4().hex
        private_write(PROJECT, '<Project Sdk="Microsoft.NET.Sdk.Web">\n'
                      '  <PropertyGroup>\n'
                      '    <TargetFramework>net10.0</TargetFramework>\n'
                      '    <ImplicitUsings>enable</ImplicitUsings>\n'
                      f'    <UserSecretsId>{secrets_id}</UserSecretsId>\n'
                      '  </PropertyGroup>\n</Project>\n')
        # A harmless template; this project is never started as a service.
        private_write(PROJECT.parent / 'Program.cs',
                      'var builder = WebApplication.CreateBuilder(args);\n'
                      'var app = builder.Build();\napp.Run();\n')


def decode_segment(value: str) -> bytes:
    return base64.urlsafe_b64decode(value + '=' * (-len(value) % 4))


def extract_key(output: str) -> bytes:
    candidates = re.findall(r"(?<![A-Za-z0-9+/])([A-Za-z0-9+/]{40,}={0,2})(?![A-Za-z0-9+/=])", output)
    decoded: list[bytes] = []
    for candidate in candidates:
        try:
            key = base64.b64decode(candidate, validate=True)
        except ValueError:
            continue
        if len(key) >= 32 and key not in decoded:
            decoded.append(key)
    if len(decoded) != 1:
        raise RuntimeError('Could not unambiguously read the SDK signing key. '
                           'Inspect dotnet user-jwts key locally; do not share its output.')
    return decoded[0]


def validate_signature(token: str, key: bytes) -> dict:
    header, payload, signature = token.split('.')
    if json.loads(decode_segment(header)).get('alg') != 'HS256':
        raise RuntimeError('Unexpected signing algorithm from SDK.')
    expected = hmac.new(key, f'{header}.{payload}'.encode('ascii'), hashlib.sha256).digest()
    if not hmac.compare_digest(expected, decode_segment(signature)):
        raise RuntimeError('Issued token does not match the exported signing key.')
    claims = json.loads(decode_segment(payload))
    if claims.get('iss') != ISSUER or AUDIENCE not in (
        claims.get('aud') if isinstance(claims.get('aud'), list) else [claims.get('aud')]
    ):
        raise RuntimeError('Unexpected issuer/audience from SDK.')
    return claims


def issue(kind: str, company: str, master_id: int | None = None,
          subject: str | None = None, admin: bool = False,
          valid_for: str = '1h') -> tuple[str, bytes, dict]:
    company = str(uuid.UUID(company))
    if not re.fullmatch(r'[1-9][0-9]*[dhms]', valid_for):
        raise ValueError('valid-for must be a positive integer followed by d/h/m/s.')
    if kind == 'user':
        if master_id is None or not 1 <= master_id <= 2147483647:
            raise ValueError('User requires a positive Int32 master-id.')
        subject = subject or f'employee-{master_id}'
    elif master_id is not None or admin or not subject:
        raise ValueError('Service requires subject, no master-id and no Admin role.')
    args = ['user-jwts', 'create', '--project', str(PROJECT), '--issuer', ISSUER,
            '--audience', AUDIENCE, '--name', subject, '--claim', f'companyId={company}',
            '--claim', f'actorType={kind}', '--valid-for', valid_for, '--output', 'token']
    if kind == 'user':
        args += ['--claim', f'masterId={master_id}', '--role', 'Employee']
        if admin:
            args += ['--role', 'Admin']
    output = run_dotnet(*args)
    tokens = TOKEN_RE.findall(output)
    if len(tokens) != 1:
        raise RuntimeError('SDK did not return one recognizable token; output withheld.')
    key = extract_key(run_dotnet('user-jwts', 'key', '--project', str(PROJECT),
                                '--issuer', ISSUER))
    claims = validate_signature(tokens[0], key)
    if claims.get('actorType') != kind or claims.get('companyId') != company:
        raise RuntimeError('Issued identity claims differ from requested claims.')
    if kind == 'user' and str(claims.get('masterId')) != str(master_id):
        raise RuntimeError('Issued masterId differs from requested value.')
    private_write(HOME / 'api.env',
                  f'Auth__Issuer={ISSUER}\nAuth__Audience={AUDIENCE}\n'
                  f'Auth__SigningKeyBase64={base64.b64encode(key).decode("ascii")}\n')
    return tokens[0], key, claims


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    commands.add_parser('prepare', help='Create common example tokens and api.env.')
    token = commands.add_parser('token', help='Issue a token to stdout (secret).')
    token.add_argument('--kind', choices=('user', 'service'), default='user')
    token.add_argument('--company', default=COMPANY)
    token.add_argument('--master-id', type=int)
    token.add_argument('--subject')
    token.add_argument('--admin', action='store_true')
    token.add_argument('--valid-for', default='1h')
    args = parser.parse_args()
    try:
        ensure_project()
        if args.command == 'token':
            value, _, _ = issue(args.kind, args.company, args.master_id,
                                args.subject, args.admin, args.valid_for)
            print(value)
            return 0
        actors = [
            ('employee123', 'user', COMPANY, 123, None, False),
            ('employee124', 'user', COMPANY, 124, None, False),
            ('employee125', 'user', COMPANY, 125, None, False),
            ('admin1', 'user', COMPANY, 1, None, True),
            ('progress-source', 'service', COMPANY, None, 'progress-source', False),
            ('shop-source', 'service', COMPANY, None, 'shop-source', False),
            ('employee123-other-company', 'user', OTHER_COMPANY, 123, None, False),
            ('admin1-other-company', 'user', OTHER_COMPANY, 1, None, True),
        ]
        manifest = []
        for filename, kind, company, master, subject, admin in actors:
            value, _, claims = issue(kind, company, master, subject, admin)
            private_write(HOME / 'tokens' / (filename + '.jwt'), value + '\n')
            manifest.append({key: claims.get(key) for key in
                             ('sub', 'companyId', 'actorType', 'masterId', 'role')})
        private_write(HOME / 'actors.json', json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')
        print('Created example tokens, api.env and actors.json under ' + str(HOME))
        print('No business data was created. Keep .local out of Git and logs.')
        return 0
    except (ValueError, RuntimeError, OSError, subprocess.SubprocessError) as error:
        print('JWT setup failed: ' + str(error), file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
