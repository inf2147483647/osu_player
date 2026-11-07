#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""酷狗接口的最小 Python 客户端（只覆盖训练场需要的两个能力：解析播放地址、下载音频）。

签名算法与播放器 `OsuPlayer.Game/KuGou/KuGouApiClient.cs`、`KuGouSigner.cs` 逐条对齐：
- Android 网关签名：md5(salt + 按 key 排序的 `k=v` 拼接 + body + salt)；
- /v5/url 的 `key`：md5(hash + signKeySalt + appid + mid + userid)；
- 概念版（lite）身份：appid 3116 / clientver 11440 / lite 专用 salt；
- 设备身份直接复用 player-state.json 里的 guid/dev，mid = md5(guid) 的十进制大整数。

注意：本文件只用于本机训练数据准备，不参与播放器运行时。
"""

from __future__ import annotations

import hashlib
import json
import os
import random
import time
import urllib.error
import urllib.parse
import urllib.request

GATEWAY = "https://gateway.kugou.com"
SONG_URL_ROUTER = "trackercdn.kugou.com"

LITE_APPID = 3116
LITE_CLIENTVER = 11440
LITE_SALT = "LnT6xpN3khm36zse0QzvmgTZ3waWdRSA"
LITE_SIGN_KEY_SALT = "185672dd44712f60bb1736df5a377e82"
LITE_PID = 411
LITE_PAGE_ID = 967177915
LITE_PPAGE_ID = "356753938,823673182,967485191"

ANDROID_UA = "Android15-1070-11083-46-0-DiscoveryDRADProtocol-wifi"


class RiskControlError(RuntimeError):
    """网关要求人机验证（errcode/status 20028 或响应带 ssa-code）。"""


class KuGouError(RuntimeError):
    pass


def default_state_path() -> str:
    return os.path.join(
        os.environ.get("APPDATA", os.path.expanduser("~")), "OsuPlayer", "player-state.json"
    )


def load_state(path: str | None = None) -> dict:
    path = path or default_state_path()

    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def mid_from_guid(guid: str) -> str:
    digest = hashlib.md5(guid.encode("utf-8")).hexdigest()
    return str(int(digest, 16))


def _signature(params: dict, body: str = "") -> str:
    joined = "".join(f"{k}={params[k]}" for k in sorted(params))
    return hashlib.md5(f"{LITE_SALT}{joined}{body}{LITE_SALT}".encode("utf-8")).hexdigest()


def _sign_key(hash_: str, mid: str, user_id: int) -> str:
    return hashlib.md5(
        f"{hash_.lower()}{LITE_SIGN_KEY_SALT}{LITE_APPID}{mid}{user_id or 0}".encode("utf-8")
    ).hexdigest()


def _cookie(session: dict) -> str:
    device = session.get("device") or {}
    parts = [
        "KUGOU_API_PLATFORM=lite",
        f"KUGOU_API_MID={mid_from_guid(device.get('guid', ''))}",
        f"KUGOU_API_GUID={device.get('guid', '')}",
        f"KUGOU_API_DEV={device.get('dev', '')}",
        "KUGOU_API_MAC=02:00:00:00:00:00",
    ]

    if session.get("token"):
        parts.append(f"token={session['token']}")

    if session.get("userid"):
        parts.append(f"userid={session['userid']}")

    return "; ".join(parts)


def _request_json(url: str, headers: dict, timeout: float = 30.0) -> tuple[dict, dict]:
    request = urllib.request.Request(url, headers=headers, method="GET")

    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            body = response.read().decode("utf-8", "replace")
            return json.loads(body), {k.lower(): v for k, v in response.headers.items()}
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")
        raise KuGouError(f"HTTP {e.code}: {body[:500]}") from e
    except urllib.error.URLError as e:
        raise KuGouError(f"网络异常: {e.reason}") from e


def _check_risk(payload: dict, headers: dict) -> None:
    code = payload.get("errcode", payload.get("status", 0))

    if "ssa-code" in headers or str(code) == "20028":
        raise RiskControlError(
            "酷狗触发安全验证（20028）。请先在播放器/酷狗 App 内完成人机验证，再继续下载。"
        )


def resolve_song_url(
    session: dict,
    hash_: str,
    album_audio_id: int = 0,
    quality: str = "128",
    free_part: bool = False,
    timeout: float = 30.0,
) -> dict:
    """调用 /v5/url，返回原始 JSON。失败抛 KuGouError，风控抛 RiskControlError。"""
    token = session.get("token") or ""
    user_id = int(session.get("userid") or 0)
    device = session.get("device") or {}
    mid = mid_from_guid(device.get("guid", ""))
    dfid = device.get("dfid") or "-"
    client_time = int(time.time() * 1000)

    params = {
        "dfid": dfid,
        "mid": mid,
        "appid": LITE_APPID,
        "clientver": LITE_CLIENTVER,
        "clienttime": client_time,
        "token": token,
        "userid": user_id or "0",
        # /v5/url 业务参数（与 KuGouApi.GetSongUrlAsync 一致）
        "album_audio_id": album_audio_id,
        "area_code": 1,
        "hash": hash_.lower(),
        "behavior": "play",
        "pid": LITE_PID,
        "cmd": 26,
        "pidversion": 3001,
        "IsFreePart": 1 if free_part else 0,
        "album_id": 0,
        "ssa_flag": "is_fromtrack",
        "version": 11430,
        "page_id": LITE_PAGE_ID,
        "quality": quality,
        "ppage_id": LITE_PPAGE_ID,
        "cdnBackup": 1,
        "module": "",
        "key": _sign_key(hash_, mid, user_id),
    }

    # clientver 在 /v5/url 上固定 11430（覆盖默认的 11440）。
    params["clientver"] = 11430
    params["signature"] = _signature(params)

    query = urllib.parse.urlencode(params)
    url = f"{GATEWAY}/v5/url?{query}"

    headers = {
        "User-Agent": ANDROID_UA,
        "dfid": dfid,
        "mid": mid,
        "clienttime": str(client_time),
        "kg-rc": "1",
        "kg-thash": "5d816a0",
        "kg-rec": "1",
        "kg-rf": "B9EDA08A64250DEFFBCADDEE00F8F25F",
        "Cookie": _cookie(session),
        "x-router": SONG_URL_ROUTER,
    }

    payload, response_headers = _request_json(url, headers, timeout)
    _check_risk(payload, response_headers)
    return payload


def pick_url(payload: dict) -> tuple[str | None, str, str]:
    """从 /v5/url 响应里取 (地址, 扩展名, 说明)。"""
    urls = payload.get("url")

    if isinstance(urls, list) and urls:
        url = urls[0]
        ext = (payload.get("extName") or os.path.splitext(urllib.parse.urlparse(url).path)[1] or "mp3")
        return url, ext.lstrip(".").lower() or "mp3", ""

    status = payload.get("status")
    reason = payload.get("errmsg") or payload.get("error") or payload.get("msg") or ""

    if status == 2:
        return None, "mp3", f"会员/付费限制（status=2）{reason}"

    return None, "mp3", f"未返回播放地址（status={status}）{reason}"


def download_file(url: str, target: str, timeout: float = 120.0) -> None:
    """流式下载到临时文件后原子改名，避免中断留下半截音频。"""
    os.makedirs(os.path.dirname(target), exist_ok=True)
    temp = target + ".part"
    request = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})

    try:
        with urllib.request.urlopen(request, timeout=timeout) as response, open(temp, "wb") as out:
            while True:
                chunk = response.read(256 * 1024)

                if not chunk:
                    break

                out.write(chunk)

        if os.path.getsize(temp) < 32 * 1024:
            raise KuGouError(f"下载内容过小（{os.path.getsize(temp)}B），可能是错误页")

        os.replace(temp, target)
    except Exception:
        if os.path.exists(temp):
            os.remove(temp)

        raise


def polite_delay(min_seconds: float = 2.5, max_seconds: float = 4.5) -> None:
    """顺序下载时的间隔：避免短时间密集请求触发风控。"""
    time.sleep(random.uniform(min_seconds, max_seconds))
