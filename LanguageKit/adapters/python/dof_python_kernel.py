import sys

from py4godot.classes import gdclass
from py4godot.classes.Node import Node


@gdclass
class dof_python_kernel(Node):
    def weighted_sum(self, a: int, b: int, c: int) -> int:
        return a + 2 * b + 3 * c

    def runtime_version(self) -> str:
        return sys.version.split()[0]
