import asyncio
from guessit.jsonutils import json
from aio_pika import Message
from aio_pika.abc import AbstractIncomingMessage
from logging import getLogger
from typing import Literal

from providers.rabbit_base import RabbitBase

logger = getLogger(__name__)


class Publisher(RabbitBase):
    QUEUE_RESCAN = "scanner.rescan"

    async def _publish(self, data: dict):
        logger.debug("Publishing %s", data)
        await self._channel.default_exchange.publish(
            Message(json.dumps(data).encode()),
            routing_key=self.QUEUE,
        )
        logger.debug("Published %s", data)

    async def add(self, path: str):
        await self._publish({"action": "scan", "path": path})

    async def delete(self, path: str):
        await self._publish({"action": "delete", "path": path})

    async def refresh(
            self,
            kind: Literal["collection", "show", "movie", "season", "episode"],
            id: str,
            **_kwargs,
    ):
        await self._publish({"action": "refresh", "kind": kind, "id": id})

    async def listen(self, scan):
        async def on_message(message: AbstractIncomingMessage):
            logger.debug("Received message: %s", message.body)

            data = json.loads(message.body)
            action = data.get("action", "")
            if action != "refresh":
                logger.debug("Ignoring message: %s", message.body)
                # Tell RabbitMQ to requeue the message, hopefully sending it to another consumer
                # that can handle it.
                # A better approach would be to use a separate queue for these messages, or
                # routing keys. However, because rabbitmq will be removed in v5, this is probably
                # not worth changing now.
                # Warning: this can cause an infinite loop if the message if no consumers can handle
                # it (usually only if the backend is unavailable).
                await message.reject(requeue=True)
                return

            try:
                await scan()
                await message.ack()
            except Exception as e:
                logger.exception("Unhandled error", exc_info=e)
                await message.reject()

        await self._queue.consume(on_message)
        await asyncio.Future()
