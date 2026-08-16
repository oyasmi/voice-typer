#!/usr/bin/env ruby
# frozen_string_literal: true

require 'fileutils'
require 'pathname'
require 'xcodeproj'

ROOT = File.expand_path('..', __dir__)
PROJECT_PATH = File.join(ROOT, 'VoiceTyper.xcodeproj')
TARGET_NAME = 'VoiceTyper'
PRODUCT_BUNDLE_IDENTIFIER = 'com.voicetyper.app'
YAMS_REPOSITORY_URL = 'https://github.com/jpsim/Yams.git'
YAMS_MINIMUM_VERSION = '6.2.1'
# 已实测验证：xcframework 含 macos-arm64_x86_64 通用切片、C API 契约、
# session.disable_prepacking 配置项均可用。见 macos/DESIGN.md §4.1。
ORT_REPOSITORY_URL = 'https://github.com/microsoft/onnxruntime-swift-package-manager'
ORT_EXACT_VERSION = '1.24.2'

FileUtils.rm_rf(PROJECT_PATH)

project = Xcodeproj::Project.new(PROJECT_PATH)
project.root_object.attributes['LastSwiftUpdateCheck'] = '2600'
project.root_object.attributes['LastUpgradeCheck'] = '2600'
project.build_configuration_list.set_setting('SWIFT_VERSION', '6.0')
project.build_configuration_list.set_setting('MACOSX_DEPLOYMENT_TARGET', '14.0')
project.build_configuration_list.set_setting('MARKETING_VERSION', '3.0.0')
project.build_configuration_list.set_setting('CURRENT_PROJECT_VERSION', '1')

app_target = project.new_target(:application, TARGET_NAME, :osx, '14.0')
app_target.product_name = TARGET_NAME

yams_package = project.new(Xcodeproj::Project::Object::XCRemoteSwiftPackageReference)
yams_package.repositoryURL = YAMS_REPOSITORY_URL
yams_package.requirement = {
  'kind' => 'upToNextMajorVersion',
  'minimumVersion' => YAMS_MINIMUM_VERSION,
}
project.root_object.package_references << yams_package

yams_product = project.new(Xcodeproj::Project::Object::XCSwiftPackageProductDependency)
yams_product.package = yams_package
yams_product.product_name = 'Yams'
app_target.package_product_dependencies << yams_product

ort_package = project.new(Xcodeproj::Project::Object::XCRemoteSwiftPackageReference)
ort_package.repositoryURL = ORT_REPOSITORY_URL
ort_package.requirement = {
  'kind' => 'exactVersion',
  'version' => ORT_EXACT_VERSION,
}
project.root_object.package_references << ort_package

ort_product = project.new(Xcodeproj::Project::Object::XCSwiftPackageProductDependency)
ort_product.package = ort_package
ort_product.product_name = 'onnxruntime'
app_target.package_product_dependencies << ort_product

app_target.build_configurations.each do |config|
  settings = config.build_settings
  settings['PRODUCT_NAME'] = TARGET_NAME
  settings['PRODUCT_BUNDLE_IDENTIFIER'] = PRODUCT_BUNDLE_IDENTIFIER
  settings['INFOPLIST_FILE'] = 'Resources/Info.plist'
  settings['GENERATE_INFOPLIST_FILE'] = 'NO'
  settings['CODE_SIGN_STYLE'] = 'Automatic'
  settings['SWIFT_VERSION'] = '6.0'
  settings['MACOSX_DEPLOYMENT_TARGET'] = '14.0'
  settings['MARKETING_VERSION'] = '3.0.0'
  settings['CURRENT_PROJECT_VERSION'] = '1'
  settings['ENABLE_HARDENED_RUNTIME'] = 'NO'
  # 只出 arm64：见 macos/DESIGN.md 决策记录 D2。
  settings['ARCHS'] = 'arm64'
  settings['ONLY_ACTIVE_ARCH'] = 'NO'
  settings['LD_RUNPATH_SEARCH_PATHS'] = ['$(inherited)', '@executable_path/../Frameworks']
  settings['SWIFT_EMIT_LOC_STRINGS'] = 'NO'
  settings['ASSETCATALOG_COMPILER_GENERATE_SWIFT_ASSET_SYMBOL_EXTENSIONS'] = 'NO'

  if config.name == 'Debug'
    settings['SWIFT_OPTIMIZATION_LEVEL'] = '-Onone'
  else
    settings['SWIFT_OPTIMIZATION_LEVEL'] = '-O'
  end
end

main_group = project.main_group
source_root_group = main_group.find_subpath('Sources', true)
source_app_group = source_root_group.find_subpath('VoiceTyper', true)
resources_group = main_group.find_subpath('Resources', true)

Dir.glob(File.join(ROOT, 'Sources/VoiceTyper/**/*.swift')).sort.each do |absolute_path|
  relative_path = Pathname.new(absolute_path).relative_path_from(Pathname.new(ROOT)).to_s
  subpath = relative_path.sub(%r{\ASources/VoiceTyper/?}, '')
  components = subpath.split('/')
  file_name = components.pop

  current_group = source_app_group
  components.each do |component|
    current_group = current_group.find_subpath(component, true)
  end

  file_ref = current_group.files.find { |ref| ref.path == file_name }
  file_ref ||= current_group.new_file(relative_path)
  app_target.add_file_references([file_ref], '-')
end

info_plist_ref = resources_group.new_file('Resources/Info.plist')
info_plist_ref.include_in_index = '0'

Dir.glob(File.join(ROOT, 'Resources/*')).sort.each do |absolute_path|
  next if File.directory?(absolute_path)

  relative_path = Pathname.new(absolute_path).relative_path_from(Pathname.new(ROOT)).to_s
  next if relative_path == 'Resources/Info.plist'

  file_name = File.basename(relative_path)
  file_ref = resources_group.files.find { |ref| ref.path == file_name }
  file_ref ||= resources_group.new_file(relative_path)
  app_target.resources_build_phase.add_file_reference(file_ref, true)
end

test_target = project.new_target(:unit_test_bundle, 'VoiceTyperTests', :osx, '14.0')
test_target.add_dependency(app_target)
# 不额外链接 onnxruntime：测试宿主是 VoiceTyper.app（BUNDLE_LOADER/TEST_HOST），
# 运行时直接复用宿主已加载的符号；重复链接会触发 "linked as a static library
# by both targets" 报错。

test_target.build_configurations.each do |config|
  settings = config.build_settings
  settings['PRODUCT_BUNDLE_IDENTIFIER'] = 'com.voicetyper.app.tests'
  settings['SWIFT_VERSION'] = '6.0'
  settings['MACOSX_DEPLOYMENT_TARGET'] = '14.0'
  settings['ARCHS'] = 'arm64'
  settings['ONLY_ACTIVE_ARCH'] = 'YES'
  settings['GENERATE_INFOPLIST_FILE'] = 'YES'
  settings['CODE_SIGN_STYLE'] = 'Automatic'
  settings['BUNDLE_LOADER'] = '$(TEST_HOST)'
  settings['TEST_HOST'] = "$(BUILT_PRODUCTS_DIR)/#{TARGET_NAME}.app/Contents/MacOS/#{TARGET_NAME}"
end

tests_root_group = main_group.find_subpath('Tests', true)
tests_group = tests_root_group.find_subpath('VoiceTyperTests', true)
fixtures_group = tests_group.find_subpath('Fixtures', true)

Dir.glob(File.join(ROOT, 'Tests/VoiceTyperTests/*.swift')).sort.each do |absolute_path|
  relative_path = Pathname.new(absolute_path).relative_path_from(Pathname.new(ROOT)).to_s
  file_name = File.basename(relative_path)
  file_ref = tests_group.files.find { |ref| ref.path == file_name }
  file_ref ||= tests_group.new_file(relative_path)
  test_target.add_file_references([file_ref], '-')
end

Dir.glob(File.join(ROOT, 'Tests/VoiceTyperTests/Fixtures/*')).sort.each do |absolute_path|
  relative_path = Pathname.new(absolute_path).relative_path_from(Pathname.new(ROOT)).to_s
  file_name = File.basename(relative_path)
  file_ref = fixtures_group.files.find { |ref| ref.path == file_name }
  file_ref ||= fixtures_group.new_file(relative_path)
  test_target.resources_build_phase.add_file_reference(file_ref, true)
end

project.save

puts "Generated #{PROJECT_PATH}"
