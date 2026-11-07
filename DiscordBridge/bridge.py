import asyncio
import datetime
import os
import re
from collections import deque

import discord
from discord.ext import tasks


PIPE = r"\\.\pipe\ULKHelper.Discord"
OWNER = int(os.environ["ULK_DISCORD_OWNER_ID"])
TOKEN = os.environ["ULK_DISCORD_TOKEN"]

intents = discord.Intents.none()
intents.dm_messages = True
client = discord.Client(intents=intents, allowed_mentions=discord.AllowedMentions.none())
seen = deque(maxlen=256)
bridge_lock = asyncio.Lock()


def request(command):
    payload = (command + "\n").encode("utf-8")
    if len(payload) > 256:
        return "指令太长。"
    with open(PIPE, "r+b", buffering=0) as pipe:
        pipe.write(payload)
        reply = pipe.readline().decode("utf-8").strip()
        reply = re.sub(r"\\([\\n])", lambda m: "\n" if m[1] == "n" else "\\", reply)
        return reply or "插件未返回结果，可发送「状态」确认。"


gateway_ready = False


def reply_chunks(text, limit=1800):
    while len(text) > limit:
        split = text.rfind("\n", 0, limit + 1)
        if split <= 0:
            split = limit
        yield text[:split]
        text = text[split:].lstrip("\n")
    if text:
        yield text


async def send_reply(channel, text):
    for chunk in reply_chunks(text):
        await channel.send(chunk)


@tasks.loop(seconds=15)
async def report_bot():
    if not gateway_ready or bridge_lock.locked():
        return
    async with bridge_lock:
        try:
            reply = await asyncio.to_thread(request, f"bot {client.user.id}")
            result_id, _, result = reply.partition(" ")
            if result_id.isascii() and result_id.isdigit() and result:
                user = client.get_user(OWNER) or await client.fetch_user(OWNER)
                await send_reply(user, result)
                await asyncio.to_thread(request, f"ack {result_id}")
        except (OSError, discord.HTTPException):
            pass


@client.event
async def on_ready():
    global gateway_ready
    gateway_ready = True
    print(f"Discord 已连接，机器人 ID {client.user.id}。", flush=True)
    if not report_bot.is_running():
        report_bot.start()


@client.event
async def on_resumed():
    global gateway_ready
    gateway_ready = True


@client.event
async def on_disconnect():
    global gateway_ready
    gateway_ready = False


@client.event
async def on_message(message):
    if message.guild is not None or message.author.bot or message.author.id != OWNER:
        return

    command = " ".join(message.content.split())
    # Reserve internal verbs for bridge status and result delivery.
    if not command or command.split()[0].lower() in {"bot", "ack"}:
        return
    if bridge_lock.locked():
        await message.channel.send("上一条指令正在处理，请稍后再发。")
        return

    async with bridge_lock:
        age = discord.utils.utcnow() - message.created_at
        if age > datetime.timedelta(seconds=30) or message.id in seen:
            return
        seen.append(message.id)
        try:
            response = await asyncio.to_thread(request, command)
        except OSError:
            response = "无法联系插件。请确认游戏已加载插件并执行 /ulk discord on；可发送「状态」确认。"

    await send_reply(message.channel, response)


client.run(TOKEN)
