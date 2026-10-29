import argparse
import copy
import sys
import unittest
from pathlib import Path

options = argparse.ArgumentParser()
options.add_argument('--source', type=Path, required=True)
options.add_argument('--api', type=Path, required=True)
args = options.parse_args()
sys.path.insert(0, str(args.source))

from scripts.extension_api_parser import BuildConfig, parse_extension_api_json
from scripts.extension_api_parser.api import GlobalConstantSpec
from scripts.extension_api_parser.builtins import BuiltinMethodSpec
from scripts.extension_api_parser.type_spec import TYPES_DB


class ApiCompatibility(unittest.TestCase):
    def method(self):
        return dict(name='size', return_type='int', is_vararg=False,
                    is_const=True, is_static=False, hash=123)

    def test_old_builtin_metadata(self):
        self.assertEqual([], BuiltinMethodSpec.parse(self.method()).hash_compatibility)

    def test_new_builtin_metadata_retained(self):
        data = self.method()
        data['hash_compatibility'] = [42, 4294967295]
        self.assertEqual(data['hash_compatibility'], BuiltinMethodSpec.parse(data).hash_compatibility)

    def test_invalid_hash_metadata_rejected(self):
        for bad in [False, 42, ['42'], [True], '42']:
            with self.subTest(value=bad), self.assertRaises(ValueError):
                BuiltinMethodSpec.parse(dict(self.method(), hash_compatibility=bad))

    def test_unknown_metadata_not_silently_discarded(self):
        with self.assertRaises(RuntimeError):
            BuiltinMethodSpec.parse(dict(self.method(), future_field=1))

    def test_global_int64_values_remain_exact(self):
        for value in [-(2**63), 2**63-1, 2**32-1]:
            parsed = GlobalConstantSpec.parse(dict(name='LIMIT', value=value))
            self.assertEqual(value, parsed.value)
            self.assertIsNone(parsed.description)

    def test_constant_type_rejected(self):
        for value in [True, 1.5, '7']:
            with self.subTest(value=value), self.assertRaises(ValueError):
                GlobalConstantSpec.parse(dict(name='LIMIT', value=value))

    def test_unknown_constant_field_rejected(self):
        with self.assertRaises(RuntimeError):
            GlobalConstantSpec.parse(dict(name='LIMIT', value=1, future_field=1))

    def test_exact_beta2_dump_all_layouts(self):
        initial = copy.copy(TYPES_DB)
        for config in BuildConfig:
            with self.subTest(config=config.value):
                TYPES_DB.clear()
                TYPES_DB.update(initial)
                result = parse_extension_api_json(args.api, config, filter_builtins=None, filter_classes=False)
                self.assertEqual(11, len(result.global_constants))
        TYPES_DB.clear()
        TYPES_DB.update(initial)


unittest.main(argv=[sys.argv[0]], verbosity=2)
