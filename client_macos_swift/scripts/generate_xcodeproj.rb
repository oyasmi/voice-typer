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

FileUtils.rm_rf(PROJECT_PATH)

project = Xcodeproj::Project.new(PROJECT_PATH)
project.root_object.attributes['LastSwiftUpdateCheck'] = '2600'
project.root_object.attributes['LastUpgradeCheck'] = '2600'
project.build_configuration_list.set_setting('SWIFT_VERSION', '6.0')
project.build_configuration_list.set_setting('MACOSX_DEPLOYMENT_TARGET', '14.0')
project.build_configuration_list.set_setting('MARKETING_VERSION', '2.0.0')
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

app_target.build_configurations.each do |config|
  settings = config.build_settings
  settings['PRODUCT_NAME'] = TARGET_NAME
  settings['PRODUCT_BUNDLE_IDENTIFIER'] = PRODUCT_BUNDLE_IDENTIFIER
  settings['INFOPLIST_FILE'] = 'Resources/Info.plist'
  settings['GENERATE_INFOPLIST_FILE'] = 'NO'
  settings['CODE_SIGN_STYLE'] = 'Automatic'
  settings['SWIFT_VERSION'] = '6.0'
  settings['MACOSX_DEPLOYMENT_TARGET'] = '14.0'
  settings['MARKETING_VERSION'] = '2.0.0'
  settings['CURRENT_PROJECT_VERSION'] = '1'
  settings['ENABLE_HARDENED_RUNTIME'] = 'NO'
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
source_app_group = source_root_group.find_subpath('VoiceTyperApp', true)
resources_group = main_group.find_subpath('Resources', true)

Dir.glob(File.join(ROOT, 'Sources/VoiceTyperApp/**/*.swift')).sort.each do |absolute_path|
  relative_path = Pathname.new(absolute_path).relative_path_from(Pathname.new(ROOT)).to_s
  subpath = relative_path.sub(%r{\ASources/VoiceTyperApp/?}, '')
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

project.save

puts "Generated #{PROJECT_PATH}"
