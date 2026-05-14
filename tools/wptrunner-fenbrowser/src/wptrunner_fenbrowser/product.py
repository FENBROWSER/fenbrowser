# mypy: allow-untyped-defs

from wptrunner.browsers.base import (
    WebDriverBrowser,
    get_timeout_multiplier,
    require_arg,
)
from wptrunner.executors import executor_kwargs as base_executor_kwargs
from wptrunner.executors.base import PytestExecutor
from wptrunner.executors.executorwebdriver import (
    WebDriverCrashtestExecutor,
    WebDriverRefTestExecutor,
    WebDriverTestharnessExecutor,
)
from wptrunner.products import Product


class FenBrowserBrowser(WebDriverBrowser):
    def make_command(self):
        return [self.webdriver_binary, "--port", str(self.port)] + self.webdriver_args


def check_args(**kwargs):
    require_arg(kwargs, "binary")
    require_arg(kwargs, "webdriver_binary")


def browser_kwargs(logger, test_type, run_info_data, config, **kwargs):
    return {
        "binary": kwargs["binary"],
        "webdriver_binary": kwargs["webdriver_binary"],
        "webdriver_args": kwargs.get("webdriver_args"),
    }


def executor_kwargs(logger, test_type, test_environment, run_info_data, **kwargs):
    kwargs_for_executor = base_executor_kwargs(
        test_type,
        test_environment,
        run_info_data,
        **kwargs,
    )
    kwargs_for_executor["capabilities"] = {}
    return kwargs_for_executor


def env_extras(**kwargs):
    return []


def get_product():
    return Product(
        name="fenbrowser",
        browser_classes={None: FenBrowserBrowser},
        check_args=check_args,
        get_browser_kwargs=browser_kwargs,
        get_executor_kwargs=executor_kwargs,
        env_options={},
        get_env_extras=env_extras,
        get_timeout_multiplier=get_timeout_multiplier,
        executor_classes={
            "testharness": WebDriverTestharnessExecutor,
            "reftest": WebDriverRefTestExecutor,
            "wdspec": PytestExecutor,
            "crashtest": WebDriverCrashtestExecutor,
            "test262": WebDriverTestharnessExecutor,
        },
    )
