import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';

const root = process.cwd();
const miniRoot = path.join(root, 'miniprogram');
const readJson = (relativePath) => JSON.parse(fs.readFileSync(path.join(root, relativePath), 'utf8'));

test('微信小程序项目配置包含固定 AppID 和导入根目录', () => {
  const config = readJson('miniprogram/project.config.json');
  assert.equal(config.appid, 'wx5b0b9396b20e002e');
  assert.equal(config.compileType, 'miniprogram');
  assert.equal(config.miniprogramRoot, './');
});

test('应用配置只注册首页并提供导航标题', () => {
  const config = readJson('miniprogram/app.json');
  assert.deepEqual(config.pages, ['pages/index/index']);
  assert.equal(typeof config.window?.navigationBarTitleText, 'string');
  assert.notEqual(config.window.navigationBarTitleText.trim(), '');
});

test('应用入口和全局样式存在', () => {
  assert.equal(fs.existsSync(path.join(miniRoot, 'app.js')), true);
  assert.equal(fs.existsSync(path.join(miniRoot, 'app.wxss')), true);
});

test('私有开发者配置不在仓库中且已被忽略', () => {
  const privateConfig = path.join(miniRoot, 'project.private.config.json');
  assert.equal(fs.existsSync(privateConfig), false);
  const ignore = fs.readFileSync(path.join(root, '.gitignore'), 'utf8');
  assert.match(ignore, /miniprogram\/project\.private\.config\.json/);
});
